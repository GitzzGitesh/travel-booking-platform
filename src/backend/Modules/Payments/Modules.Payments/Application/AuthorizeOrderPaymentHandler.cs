using Microsoft.Extensions.Logging;
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

    /// <summary>How many attempts this order has had, whatever their outcome.</summary>
    Task<int> CountForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>How many attempts this customer started since <paramref name="since"/>, across all orders.</summary>
    Task<int> CountForCustomerSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Records a refusal by the attempt limits, saved with the next <see cref="TrySaveAsync"/> (append-only).</summary>
    void RecordLimitTrip(AttemptLimitTrip trip);

    Task<int> CountLimitTripsSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Customers with at least <paramref name="minimumTrips"/> refusals since <paramref name="since"/>: the review list.</summary>
    Task<IReadOnlyList<CustomerTrips>> FindCustomersToReviewAsync(DateTimeOffset since, int minimumTrips, CancellationToken cancellationToken);

    Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken);

    /// <summary>Records a consumed integration event, saved with the next <see cref="TrySaveAsync"/> (ADR 0007 inbox).</summary>
    void MarkConsumed(Guid messageId, string handler, DateTimeOffset at);
}

/// <summary>A refusal by the attempt limits: an append-only record for the review list and alerting (Q10).</summary>
internal sealed record AttemptLimitTrip(string CustomerId, Guid OrderId, string IdempotencyKey, DateTimeOffset At)
{
    public long Id { get; private set; }
}

/// <summary>A customer on the review list, with their refusals in the period.</summary>
internal sealed record CustomerTrips(string CustomerId, int Trips);

/// <summary>
/// The review list (Q10): customers who reached the payment attempt limit repeatedly in the last 24 hours, for the fraud
/// and operations team. Accounts are never blocked automatically in the MVP. Its admin endpoint comes with staff identity.
/// </summary>
internal sealed class PaymentAttemptReviewList(IPaymentAttemptStore store, TimeProvider timeProvider, Microsoft.Extensions.Options.IOptions<PaymentAttemptLimits> limits)
{
    public Task<IReadOnlyList<CustomerTrips>> FindAsync(CancellationToken cancellationToken) =>
        store.FindCustomersToReviewAsync(timeProvider.GetUtcNow() - PaymentAttemptLimits.CustomerWindow, limits.Value.AlertAfterTrips, cancellationToken);
}

/// <summary>
/// <c>Payments:AttemptLimits</c> (Q10, approved 2026-09-28): the platform's guardrail against card testing and repeated
/// authorization; Stripe Radar (with 3-D Secure) is the fraud engine. Counted from stored attempts, so it survives
/// restarts. The values are configurable, so operations can revise them.
/// </summary>
internal sealed class PaymentAttemptLimits
{
    public const string SectionName = "Payments:AttemptLimits";

    public static readonly TimeSpan CustomerWindow = TimeSpan.FromHours(24);

    /// <summary>Attempts (of any outcome) one order may have.</summary>
    public int MaxAttemptsPerOrder { get; set; } = 5;

    /// <summary>Attempts one customer may start across all orders in the last 24 hours.</summary>
    public int MaxAttemptsPerCustomerPerDay { get; set; } = 10;

    /// <summary>
    /// Refusals ("trips") by one customer within 24 hours that raise an operational alert and put them on the review
    /// list (Q10: no automatic account block in the MVP).
    /// </summary>
    public int AlertAfterTrips { get; set; } = 3;
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
internal sealed partial class AuthorizeOrderPaymentHandler(
    IPaymentAttemptStore store,
    PaymentOperations operations,
    TimeProvider timeProvider,
    IOptions<PaymentReconciliationOptions> reconciliation,
    IOptions<PaymentAttemptLimits>? limits = null,
    ILogger<AuthorizeOrderPaymentHandler>? logger = null) : IOrderPayments
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

        // A durable guardrail (counted from the stored attempts): never a new authorization past the limits.
        if (await LimitReachedAsync(request, cancellationToken))
        {
            return Failure(OrderPaymentFailure.AttemptLimitReached);
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
    internal Task<(PaymentAttempt Attempt, PaymentOutcome Outcome)> LookUpAsync(PaymentAttempt attempt, string actor, string? correlationId, CancellationToken cancellationToken) =>
        LookUpAsync(attempt, attempt.KnownProviderPayment(), actor, correlationId, cancellationToken);

    /// <param name="knownPayment">The provider's payment id, if known: stored, or a notification's hint (checked against our reference).</param>
    internal async Task<(PaymentAttempt Attempt, PaymentOutcome Outcome)> LookUpAsync(
        PaymentAttempt attempt, ProviderPaymentRef? knownPayment, string actor, string? correlationId, CancellationToken cancellationToken)
    {
        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), knownPayment, cancellationToken);
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

    private async Task<bool> LimitReachedAsync(OrderPaymentRequest request, CancellationToken cancellationToken)
    {
        var settings = limits?.Value ?? new PaymentAttemptLimits();
        var forOrder = await store.CountForOrderAsync(request.OrderId, cancellationToken);
        var forCustomer = await store.CountForCustomerSinceAsync(request.CustomerId, timeProvider.GetUtcNow() - PaymentAttemptLimits.CustomerWindow, cancellationToken);
        if (forOrder < settings.MaxAttemptsPerOrder && forCustomer < settings.MaxAttemptsPerCustomerPerDay)
        {
            return false;
        }

        // A security event (security rules: suspicious payment velocity), with our ids only; and a durable trip, so
        // repeated trips raise an operational alert and put the customer on the review list. One trip per request key
        // (a unique index): a retried refusal is not a new one. The refusal stands even if the trip cannot be recorded.
        var now = timeProvider.GetUtcNow();
        if (logger is not null)
        {
            LogLimitReached(logger, request.OrderId, request.CustomerId, forOrder, forCustomer);
        }

        var trips = 0;
        try
        {
            store.RecordLimitTrip(new AttemptLimitTrip(request.CustomerId, request.OrderId, request.IdempotencyKey, now));
            if (await store.TrySaveAsync(cancellationToken))
            {
                trips = await store.CountLimitTripsSinceAsync(request.CustomerId, now - PaymentAttemptLimits.CustomerWindow, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && logger is not null)
        {
            LogTripNotRecorded(logger, request.OrderId, exception.GetType().Name);
        }

        // Once per window, when the threshold is reached, not on every later trip (the review list shows the rest).
        if (trips == settings.AlertAfterTrips && logger is not null)
        {
            LogRepeatedTrips(logger, request.CustomerId, trips);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "PaymentAttemptLimitTripNotRecorded", Message = "The payment attempt limit refusal for order {OrderId} could not be recorded ({ExceptionType}); refused anyway")]
    private static partial void LogTripNotRecorded(ILogger logger, Guid orderId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, EventName = "PaymentAttemptLimitRepeated", Message = "Alert: customer {CustomerId} reached the payment attempt limit {Trips} times in 24 hours; on the review list")]
    private static partial void LogRepeatedTrips(ILogger logger, string customerId, int trips);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "PaymentAttemptLimitReached", Message = "Security: payment attempt limit reached for order {OrderId}, customer {CustomerId} ({OrderAttempts} for the order, {CustomerAttempts} for the customer in 24 hours); no attempt started")]
    private static partial void LogLimitReached(ILogger logger, Guid orderId, string customerId, int orderAttempts, int customerAttempts);

    private static OrderPaymentResult Report(PaymentAttempt attempt, PaymentOutcome? outcome) =>
        new(
            attempt.Id,
            attempt.Status switch
            {
                // Being released (Orders said it will not use it): never booked on, and no challenge to complete.
                PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired when attempt.ReleaseRequestedAt is not null => OrderPaymentStatus.Pending,
                PaymentAttemptStatus.Authorized or PaymentAttemptStatus.Capturing or PaymentAttemptStatus.CaptureUnknown or PaymentAttemptStatus.Captured => OrderPaymentStatus.Authorized,
                PaymentAttemptStatus.ActionRequired => OrderPaymentStatus.ActionRequired,
                PaymentAttemptStatus.Declined => OrderPaymentStatus.Declined,
                PaymentAttemptStatus.ManualReview => OrderPaymentStatus.ManualReview,
                // A void in progress may still hold funds: never booked on, never a new attempt yet.
                PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown
                    or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown => OrderPaymentStatus.Pending,
                _ => OrderPaymentStatus.Failed,
            },
            attempt.Amount,
            attempt.Status == PaymentAttemptStatus.ActionRequired && attempt.ReleaseRequestedAt is null && outcome is PaymentOutcome.ActionRequired action
                ? action.Payment.CustomerActionToken?.Value
                : null);

    private static Result<OrderPaymentResult, OrderPaymentFailure> Success(OrderPaymentResult result) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Success(result);

    private static Result<OrderPaymentResult, OrderPaymentFailure> Failure(OrderPaymentFailure failure) =>
        Result<OrderPaymentResult, OrderPaymentFailure>.Failure(failure);
}
