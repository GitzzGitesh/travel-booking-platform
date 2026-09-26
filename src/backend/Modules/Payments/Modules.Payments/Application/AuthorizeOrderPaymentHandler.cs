using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>Persistence port for payment attempts; implemented in Infrastructure (architecture rules).</summary>
internal interface IPaymentAttemptStore
{
    Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the attempt; false if the order already has an attempt with this key, or another live attempt (unique
    /// constraints).
    /// </summary>
    Task<bool> TryAddAsync(PaymentAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Saves a loaded attempt; false if another request changed it first (optimistic concurrency).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>Attempts whose outcome is not known yet, oldest first: the reconciliation job's work list.</summary>
    Task<IReadOnlyList<Guid>> FindUnresolvedAsync(DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken);
}

internal sealed class PaymentReconciliationOptions
{
    public const string SectionName = "Payments:Reconciliation";

    /// <summary>
    /// How long after an authorization attempt a provider's "not found" is conclusive (its lookup can lag its writes).
    /// Provider-specific: the real provider's ADR states it. The mock is consistent at once.
    /// </summary>
    public TimeSpan NotFoundConclusiveAfter { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// <see cref="IOrderPayments"/>: authorizes an order's total as a persisted, idempotent payment attempt. Exactly one
/// provider authorization per attempt, ever; any later request for the same attempt looks it up instead (never blindly
/// retry payment writes). <see cref="ReconcileAsync"/> is the same lookup for the reconciliation job.
/// </summary>
internal sealed class AuthorizeOrderPaymentHandler(
    IPaymentAttemptStore store,
    PaymentOperations operations,
    TimeProvider timeProvider,
    IOptions<PaymentReconciliationOptions> reconciliation) : IOrderPayments
{
    public const int MaxIdempotencyKeyLength = 100;

    // Concurrent writers are few (a repeated request, the reconciliation job): re-applying on a fresh copy converges.
    private const int _saveAttempts = 3;

    public async Task<Result<OrderPaymentResult, OrderPaymentFailure>> AuthorizeAsync(OrderPaymentRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount.Amount <= 0
            || request.CustomerId is not { Length: > 0 and <= PaymentAttempt.MaxCustomerIdLength } || string.IsNullOrWhiteSpace(request.CustomerId)
            || request.IdempotencyKey is not { Length: > 0 and <= MaxIdempotencyKeyLength } || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Failure(OrderPaymentFailure.InvalidRequest);
        }

        if (await store.FindByKeyAsync(request.OrderId, request.IdempotencyKey, cancellationToken) is { } existing)
        {
            return await ResumeAsync(existing, request, cancellationToken);
        }

        PaymentMethodToken token;
        try
        {
            token = new PaymentMethodToken(request.PaymentMethodToken);
        }
        catch (ArgumentException)
        {
            return Failure(OrderPaymentFailure.InvalidPaymentMethod); // never echoed or logged: it may be a card number
        }

        // Saved before the provider call: whatever happens next, this attempt can be found and looked up.
        var attempt = PaymentAttempt.Start(request.OrderId, request.CustomerId, request.IdempotencyKey, request.Amount, timeProvider.GetUtcNow());
        if (!await store.TryAddAsync(attempt, cancellationToken))
        {
            // The same request won the race (resume it), or another attempt for this order may still hold funds.
            return await store.FindByKeyAsync(request.OrderId, request.IdempotencyKey, cancellationToken) is { } winner
                ? await ResumeAsync(winner, request, cancellationToken)
                : Failure(OrderPaymentFailure.PaymentInProgress);
        }

        var outcome = await operations.AuthorizeAsync(new AuthorizationDetails(new PaymentReference(attempt.Reference), attempt.Amount, token), cancellationToken);
        return Report(await ApplyAndSaveAsync(attempt, outcome, fromLookup: false, cancellationToken), outcome);
    }

    /// <summary>
    /// Looks an unresolved attempt up with the provider by our reference and records what it says. Returns the attempt
    /// as stored afterwards, or null if there is no such attempt.
    /// </summary>
    public async Task<PaymentAttempt?> ReconcileAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(attemptId, cancellationToken) is not { } attempt)
        {
            return null;
        }

        if (attempt.IsFinal)
        {
            return attempt;
        }

        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), cancellationToken);
        return await ApplyAndSaveAsync(attempt, outcome, fromLookup: true, cancellationToken);
    }

    /// <summary>What an attempt becomes, given a provider outcome (payment-lifecycle.md).</summary>
    internal static (PaymentAttemptStatus Status, string Reason) Transition(PaymentAttempt attempt, PaymentOutcome outcome, bool fromLookup, TimeSpan notFoundConclusiveAfter) =>
        outcome switch
        {
            // A lookup checks only our reference: a payment for another amount is not this attempt's to act on.
            _ when Snapshot(outcome) is { } payment && payment.Amount != attempt.Amount
                => (PaymentAttemptStatus.ManualReview, "The provider reports a different amount"),
            PaymentOutcome.Authorized => (PaymentAttemptStatus.Authorized, "Authorized"),
            PaymentOutcome.ActionRequired => (PaymentAttemptStatus.ActionRequired, "Customer challenge required"),
            PaymentOutcome.Declined declined => (PaymentAttemptStatus.Declined, $"Declined ({declined.Reason})"),
            PaymentOutcome.Canceled => (PaymentAttemptStatus.Canceled, "Canceled by the provider or the customer"),
            PaymentOutcome.AuthorizationExpired => (PaymentAttemptStatus.Expired, "Authorization expired"),
            PaymentOutcome.Unknown unknown => (PaymentAttemptStatus.AuthorizationUnknown, $"Outcome unknown ({unknown.Cause}); to be looked up"),

            // Refused as invalid on the first call: nothing is held. A lookup failing the same way proves nothing.
            PaymentOutcome.Rejected rejected when !fromLookup
                => (PaymentAttemptStatus.Failed, $"Rejected ({rejected.Reason})"),
            PaymentOutcome.Rejected rejected => (PaymentAttemptStatus.AuthorizationUnknown, $"Lookup rejected ({rejected.Reason}); to be looked up again"),

            // The provider's lookup can lag its writes: "not found" is conclusive only after its consistency window.
            PaymentOutcome.NotFound notFound when notFound.At - attempt.CreatedAt >= notFoundConclusiveAfter
                => (PaymentAttemptStatus.Failed, "Not found at the provider after its consistency window"),
            PaymentOutcome.NotFound => (PaymentAttemptStatus.AuthorizationUnknown, "Not found yet; inside the provider's consistency window"),
            PaymentOutcome.Mismatch => (PaymentAttemptStatus.ManualReview, "The provider reported something other than what was requested"),

            // Capture, void and refund outcomes do not belong to the authorization phase.
            _ => (PaymentAttemptStatus.ManualReview, $"Unexpected provider outcome {outcome.GetType().Name}"),
        };

    private async Task<Result<OrderPaymentResult, OrderPaymentFailure>> ResumeAsync(PaymentAttempt existing, OrderPaymentRequest request, CancellationToken cancellationToken)
    {
        if (existing.Amount != request.Amount || existing.CustomerId != request.CustomerId)
        {
            return Failure(OrderPaymentFailure.IdempotencyKeyReused);
        }

        if (existing.IsFinal)
        {
            return Report(existing, null);
        }

        // Unknown, in flight or crashed while Authorizing, or waiting for a challenge: look it up, never authorize again.
        var outcome = await operations.ReconcileAsync(new PaymentReference(existing.Reference), cancellationToken);
        return Report(await ApplyAndSaveAsync(existing, outcome, fromLookup: true, cancellationToken), outcome);
    }

    private async Task<PaymentAttempt> ApplyAndSaveAsync(PaymentAttempt attempt, PaymentOutcome outcome, bool fromLookup, CancellationToken cancellationToken)
    {
        var window = reconciliation.Value.NotFoundConclusiveAfter;
        var snapshot = Snapshot(outcome);
        var declineReason = (outcome as PaymentOutcome.Declined)?.Reason.ToString();
        for (var round = 1; ; round++)
        {
            var (status, reason) = Transition(attempt, outcome, fromLookup, window);
            if (!attempt.Resolve(status, reason, timeProvider.GetUtcNow(), snapshot?.Payment.ProviderId, snapshot?.Payment.Value, declineReason))
            {
                return attempt; // already final: a late outcome changes nothing
            }

            if (await store.TrySaveAsync(cancellationToken))
            {
                return attempt;
            }

            // Another request recorded something first: apply this outcome to what is stored now.
            attempt = await store.FindAsync(attempt.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Payment attempt {attempt.Id} disappeared.");
            if (round == _saveAttempts)
            {
                return attempt;
            }
        }
    }

    private static PaymentSnapshot? Snapshot(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Authorized a => a.Payment,
        PaymentOutcome.ActionRequired a => a.Payment,
        PaymentOutcome.Canceled c => c.Payment,
        PaymentOutcome.AuthorizationExpired e => e.Payment,
        PaymentOutcome.Mismatch m => m.Payment,
        _ => null,
    };

    private static Result<OrderPaymentResult, OrderPaymentFailure> Report(PaymentAttempt attempt, PaymentOutcome? outcome) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Success(new OrderPaymentResult(
            attempt.Id,
            attempt.Status switch
            {
                PaymentAttemptStatus.Authorized => OrderPaymentStatus.Authorized,
                PaymentAttemptStatus.ActionRequired => OrderPaymentStatus.ActionRequired,
                PaymentAttemptStatus.Declined => OrderPaymentStatus.Declined,
                PaymentAttemptStatus.ManualReview => OrderPaymentStatus.ManualReview,
                PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown => OrderPaymentStatus.Pending,
                _ => OrderPaymentStatus.Failed,
            },
            attempt.Status == PaymentAttemptStatus.Declined ? attempt.DeclineReason : null,
            attempt.Status == PaymentAttemptStatus.ActionRequired && outcome is PaymentOutcome.ActionRequired action
                ? action.Payment.CustomerActionToken?.Value
                : null));

    private static Result<OrderPaymentResult, OrderPaymentFailure> Failure(OrderPaymentFailure failure) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Failure(failure);
}
