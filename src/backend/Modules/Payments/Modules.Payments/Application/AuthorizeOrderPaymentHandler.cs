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

    /// <summary>
    /// Saves the tracked changes; false if another request changed the attempt first (optimistic concurrency), or an
    /// inbox record already exists (a duplicate delivery). Nothing is saved then.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>The order's live attempt (at most one: a filtered unique index), untracked.</summary>
    Task<PaymentAttempt?> FindLiveByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// The reconciliation work list, oldest first: open or voiding attempts untouched since <paramref name="settledBefore"/>,
    /// and Authorized attempts whose release was requested.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindReconcilableAsync(DateTimeOffset settledBefore, int limit, CancellationToken cancellationToken);

    Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken);

    /// <summary>Records a consumed integration event, saved with the next <see cref="TrySaveAsync"/> (ADR 0007 inbox).</summary>
    void MarkConsumed(Guid messageId, string handler, DateTimeOffset at);
}

internal sealed class PaymentReconciliationOptions
{
    public const string SectionName = "Payments:Reconciliation";

    /// <summary>
    /// How long after an authorization attempt a provider's "not found" is conclusive (its lookup can lag its writes).
    /// Provider-specific: the real provider's ADR states it. The mock is consistent at once.
    /// </summary>
    public TimeSpan NotFoundConclusiveAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an open attempt is left alone after its last change before the reconciliation job looks it up: a request
    /// may still be waiting for the provider, and an unfinished challenge is not looked up more often than this.
    /// </summary>
    public TimeSpan LookupAfter { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// <see cref="IOrderPayments"/>: authorizes an order's total as a persisted, idempotent payment attempt. Exactly one
/// provider authorization per attempt, ever; any later request for the same attempt looks it up instead (never blindly
/// retry payment writes).
/// </summary>
internal sealed class AuthorizeOrderPaymentHandler(
    IPaymentAttemptStore store,
    PaymentOperations operations,
    TimeProvider timeProvider,
    IOptions<PaymentReconciliationOptions> reconciliation) : IOrderPayments
{
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>The actor on the attempt's history: the customer the attempt belongs to (the row names them).</summary>
    internal const string CustomerActor = "customer";

    // Concurrent writers are few (a repeated request, a later lookup): re-applying on a fresh copy converges.
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
            return await ResumeMatchingAsync(existing, request, cancellationToken);
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
        var attempt = PaymentAttempt.Start(request.OrderId, request.CustomerId, request.IdempotencyKey, request.Amount, Change(CustomerActor, request.CorrelationId));
        if (!await store.TryAddAsync(attempt, cancellationToken))
        {
            // The same request won the race (resume it), or another attempt for this order may still hold funds.
            return await store.FindByKeyAsync(request.OrderId, request.IdempotencyKey, cancellationToken) is { } winner
                ? await ResumeMatchingAsync(winner, request, cancellationToken)
                : Failure(OrderPaymentFailure.PaymentInProgress);
        }

        var outcome = await operations.AuthorizeAsync(new AuthorizationDetails(new PaymentReference(attempt.Reference), attempt.Amount, token), cancellationToken);
        return Success(Report(await ApplyAndSaveAsync(attempt, outcome, fromLookup: false, CustomerActor, request.CorrelationId, cancellationToken), outcome));
    }

    public async Task<OrderPaymentResult?> ResumeAsync(Guid orderId, string customerId, string idempotencyKey, string? correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || await store.FindByKeyAsync(orderId, idempotencyKey, cancellationToken) is not { } attempt
            || attempt.CustomerId != customerId)
        {
            return null;
        }

        return await BringUpToDateAsync(attempt, correlationId, cancellationToken);
    }

    public async Task<LiveOrderPayment?> FindLiveAsync(Guid orderId, CancellationToken cancellationToken) =>
        await store.FindLiveByOrderAsync(orderId, cancellationToken) is { } attempt
            ? new LiveOrderPayment(attempt.Id, Report(attempt, null).Status, attempt.ReleaseRequestedAt is not null)
            : null;

    /// <summary>
    /// Looks an open attempt up with the provider by our reference and records the outcome (never a second
    /// authorization). Returns the attempt as stored afterwards, with the outcome.
    /// </summary>
    internal async Task<(PaymentAttempt Attempt, PaymentOutcome Outcome)> LookUpAsync(PaymentAttempt attempt, string actor, string? correlationId, CancellationToken cancellationToken)
    {
        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), cancellationToken);
        return (await ApplyAndSaveAsync(attempt, outcome, fromLookup: true, actor, correlationId, cancellationToken), outcome);
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

    private async Task<Result<OrderPaymentResult, OrderPaymentFailure>> ResumeMatchingAsync(PaymentAttempt existing, OrderPaymentRequest request, CancellationToken cancellationToken)
    {
        if (existing.Amount != request.Amount || existing.CustomerId != request.CustomerId)
        {
            return Failure(OrderPaymentFailure.IdempotencyKeyReused);
        }

        return Success(await BringUpToDateAsync(existing, request.CorrelationId, cancellationToken));
    }

    /// <summary>A settled attempt as it is; an open one (unknown, in flight or crashed, or challenged) looked up, never authorized again.</summary>
    private async Task<OrderPaymentResult> BringUpToDateAsync(PaymentAttempt attempt, string? correlationId, CancellationToken cancellationToken)
    {
        if (attempt.IsAuthorizationSettled)
        {
            return Report(attempt, null);
        }

        var (current, outcome) = await LookUpAsync(attempt, CustomerActor, correlationId, cancellationToken);
        return Report(current, outcome);
    }

    private async Task<PaymentAttempt> ApplyAndSaveAsync(PaymentAttempt attempt, PaymentOutcome outcome, bool fromLookup, string actor, string? correlationId, CancellationToken cancellationToken)
    {
        var window = reconciliation.Value.NotFoundConclusiveAfter;
        var snapshot = Snapshot(outcome);
        var declineReason = (outcome as PaymentOutcome.Declined)?.Reason.ToString();
        for (var round = 1; ; round++)
        {
            var (status, reason) = Transition(attempt, outcome, fromLookup, window);
            if (!attempt.Resolve(status, reason, Change(actor, correlationId), snapshot?.Payment.ProviderId, snapshot?.Payment.Value, declineReason).IsSuccess)
            {
                return attempt; // already settled: a late outcome changes nothing
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

    private PaymentChange Change(string actor, string? correlationId) => new(timeProvider.GetUtcNow(), actor, correlationId);

    private static PaymentSnapshot? Snapshot(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Authorized a => a.Payment,
        PaymentOutcome.ActionRequired a => a.Payment,
        PaymentOutcome.Canceled c => c.Payment,
        PaymentOutcome.AuthorizationExpired e => e.Payment,
        PaymentOutcome.Mismatch m => m.Payment,
        _ => null,
    };

    private static OrderPaymentResult Report(PaymentAttempt attempt, PaymentOutcome? outcome) =>
        new(
            attempt.Id,
            attempt.Status switch
            {
                // Being released (Orders said it will not use it): never booked on, and no challenge to complete.
                PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired when attempt.ReleaseRequestedAt is not null => OrderPaymentStatus.Pending,
                PaymentAttemptStatus.Authorized => OrderPaymentStatus.Authorized,
                PaymentAttemptStatus.ActionRequired => OrderPaymentStatus.ActionRequired,
                PaymentAttemptStatus.Declined => OrderPaymentStatus.Declined,
                PaymentAttemptStatus.ManualReview => OrderPaymentStatus.ManualReview,
                // A void in progress may still hold funds: never booked on, never a new attempt yet.
                PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown
                    or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown => OrderPaymentStatus.Pending,
                _ => OrderPaymentStatus.Failed,
            },
            attempt.Amount,
            attempt.Status == PaymentAttemptStatus.Declined ? attempt.DeclineReason : null,
            attempt.Status == PaymentAttemptStatus.ActionRequired && attempt.ReleaseRequestedAt is null && outcome is PaymentOutcome.ActionRequired action
                ? action.Payment.CustomerActionToken?.Value
                : null);

    private static Result<OrderPaymentResult, OrderPaymentFailure> Success(OrderPaymentResult result) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Success(result);

    private static Result<OrderPaymentResult, OrderPaymentFailure> Failure(OrderPaymentFailure failure) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Failure(failure);
}
