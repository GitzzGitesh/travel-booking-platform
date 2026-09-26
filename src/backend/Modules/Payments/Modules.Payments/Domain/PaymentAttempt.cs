using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Domain;

/// <summary>
/// One payment attempt (payment-lifecycle.md). Capture and refunds come with the orchestration chunk that needs them.
/// Authorization: Authorizing → any authorization outcome; ActionRequired and AuthorizationUnknown → any outcome but
/// Authorizing (a completed challenge, a lookup).
/// Release: Authorized or ActionRequired → Voiding (saved before the provider call) → Voided, Canceled (an unfinished
/// challenge), VoidUnknown (looked up, then voided again with the same key) or ManualReview.
/// Declined, Canceled, Expired, Failed and Voided are final.
/// </summary>
internal enum PaymentAttemptStatus
{
    Authorizing,
    ActionRequired,
    AuthorizationUnknown,
    Authorized,
    Declined,
    Canceled,
    Expired,
    Failed,
    ManualReview,
    Voiding,
    VoidUnknown,
    Voided,
}

internal enum PaymentAttemptTransitionError
{
    /// <summary>The attempt's outcome is settled: a late or duplicate outcome changes nothing.</summary>
    AlreadyFinal,

    /// <summary>Not a transition this attempt can make (e.g. back to Authorizing, or voiding a declined payment).</summary>
    Illegal,
}

/// <summary>
/// One attempt to authorize an order's total. It is saved as Authorizing BEFORE the provider is called, so a crash
/// never loses a possible hold: the attempt can always be looked up by its reference. Likewise a void is saved as
/// Voiding before the provider is asked. Status changes only through the methods here, each appended to the attempt's
/// history with its actor and correlation id.
/// </summary>
internal sealed class PaymentAttempt
{
    public const int MaxCustomerIdLength = 128;
    public const int MaxReleaseReasonLength = 200;

    private readonly List<PaymentAttemptEvent> _events = [];

    private PaymentAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Unique per order: the same request returns this attempt.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    public Money Amount { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    public string? ProviderId { get; private set; }

    /// <summary>The provider's payment id, once known.</summary>
    public string? ProviderPaymentId { get; private set; }

    public string? DeclineReason { get; private set; }

    /// <summary>When Orders said it will not use this payment: the hold is to be released once its outcome is known.</summary>
    public DateTimeOffset? ReleaseRequestedAt { get; private set; }

    public string? ReleaseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Incremented by every change, so the rowversion check always runs.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<PaymentAttemptEvent> Events => _events;

    /// <summary>Our reference at the provider: this attempt's id, without dashes.</summary>
    public string Reference => Id.ToString("N");

    /// <summary>Our idempotency key for the one void of this attempt: repeating it never releases twice.</summary>
    public string VoidKey => $"{Reference}:void";

    /// <summary>
    /// Statuses in which the attempt holds, or may hold, funds: at most one such attempt per order. Each has a way out: a
    /// lookup (the open ones and the void in progress), a void (Authorized), or a person (ManualReview).
    /// </summary>
    public static IReadOnlyList<PaymentAttemptStatus> LiveStatuses { get; } =
    [
        PaymentAttemptStatus.Authorizing, PaymentAttemptStatus.ActionRequired, PaymentAttemptStatus.AuthorizationUnknown,
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.ManualReview, PaymentAttemptStatus.Voiding, PaymentAttemptStatus.VoidUnknown,
    ];

    /// <summary>The authorization's outcome is known (whatever happens to the hold afterwards).</summary>
    public bool IsAuthorizationSettled => Status is not (PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.AuthorizationUnknown);

    public bool IsVoidInProgress => Status is PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown;

    public static PaymentAttempt Start(Guid orderId, string customerId, string idempotencyKey, Money amount, PaymentChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment attempt authorizes a positive amount.");
        }

        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            IdempotencyKey = idempotencyKey,
            Amount = amount,
            Status = PaymentAttemptStatus.Authorizing,
            CreatedAt = change.At,
            UpdatedAt = change.At,
        };
        attempt.Record(null, "Authorization requested", change, null);
        return attempt;
    }

    /// <summary>
    /// Records what the provider established about the authorization. The same status again only fills in provider
    /// details, without a history entry. A settled attempt never changes this way, and nothing goes back to Authorizing.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> Resolve(
        PaymentAttemptStatus to, string reason, PaymentChange change, string? providerId = null, string? providerPaymentId = null, string? declineReason = null)
    {
        if (IsAuthorizationSettled)
        {
            return Failure(PaymentAttemptTransitionError.AlreadyFinal);
        }

        if (to is PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown or PaymentAttemptStatus.Voided)
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        if (providerId is not null && providerPaymentId is not null)
        {
            ProviderId = providerId;
            ProviderPaymentId = providerPaymentId;
        }

        DeclineReason = declineReason ?? DeclineReason;
        return MoveTo(to, reason, change, providerPaymentId);
    }

    /// <summary>
    /// Orders will not use this payment. The request is recorded (no status change) and acted on once the outcome is
    /// known. Idempotent. False when the attempt holds nothing any more, so there is nothing to release.
    /// </summary>
    public bool RequestRelease(string reason, PaymentChange change)
    {
        if (ReleaseRequestedAt is not null)
        {
            return true;
        }

        if (!LiveStatuses.Contains(Status))
        {
            return false;
        }

        ReleaseRequestedAt = change.At;
        ReleaseReason = reason.Length <= MaxReleaseReasonLength ? reason : reason[..MaxReleaseReasonLength];
        Record(Status, $"Release requested: {ReleaseReason}", change, null);
        return true;
    }

    /// <summary>
    /// Starts releasing the hold of an Authorized payment or an unfinished challenge (a void, which cancels the
    /// latter). Saved before the provider is asked. Needs the provider's payment id; a void already in progress is a no-op.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> BeginVoid(PaymentChange change)
    {
        if (Status is PaymentAttemptStatus.Voiding)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
        }

        if (Status is not (PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.VoidUnknown)
            || ProviderId is null || ProviderPaymentId is null)
        {
            return Failure(Status is PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(PaymentAttemptStatus.Voiding, "Releasing the hold (void)", change, ProviderPaymentId);
    }

    /// <summary>Records the void's outcome: Voided, Canceled (an unfinished challenge), VoidUnknown or ManualReview.</summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> ResolveVoid(PaymentAttemptStatus to, string reason, PaymentChange change)
    {
        if (!IsVoidInProgress)
        {
            return Failure(Status is PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        if (to is not (PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled or PaymentAttemptStatus.VoidUnknown or PaymentAttemptStatus.ManualReview))
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(to, reason, change, ProviderPaymentId);
    }

    private static Result<PaymentAttemptStatus, PaymentAttemptTransitionError> Failure(PaymentAttemptTransitionError error) =>
        Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Failure(error);

    private Result<PaymentAttemptStatus, PaymentAttemptTransitionError> MoveTo(PaymentAttemptStatus to, string reason, PaymentChange change, string? providerReference)
    {
        if (to == Status)
        {
            UpdatedAt = change.At;
            Revision++;
        }
        else
        {
            var from = Status;
            Status = to;
            Record(from, reason, change, providerReference);
        }

        return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
    }

    private void Record(PaymentAttemptStatus? from, string reason, PaymentChange change, string? providerReference)
    {
        _events.Add(new PaymentAttemptEvent(Id, change.At, change.Actor, from?.ToString(), Status.ToString(), reason, change.CorrelationId, providerReference));
        UpdatedAt = change.At;
        Revision++;
    }
}

/// <summary>Who changed an attempt, and when, with the request's correlation id (security rules: payments are audited).</summary>
internal sealed record PaymentChange(DateTimeOffset At, string Actor, string? CorrelationId);

/// <summary>An append-only record of one attempt transition. Never updated or deleted.</summary>
internal sealed record PaymentAttemptEvent(
    Guid PaymentAttemptId, DateTimeOffset At, string Actor, string? FromStatus, string ToStatus, string Reason, string? CorrelationId, string? ProviderReference)
{
    public long Id { get; private set; }
}
