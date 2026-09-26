using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Domain;

/// <summary>
/// The authorization phase of one payment attempt (payment-lifecycle.md). Capture, void and refunds come with the
/// orchestration chunk that needs them.
/// Authorizing → any other status; ActionRequired and AuthorizationUnknown → any status but Authorizing (a completed
/// challenge, a lookup); every other status is final.
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
}

internal enum PaymentAttemptTransitionError
{
    /// <summary>The attempt's outcome is settled: a late or duplicate outcome changes nothing.</summary>
    AlreadyFinal,

    /// <summary>Nothing goes back to Authorizing: an attempt is authorized at the provider once.</summary>
    Illegal,
}

/// <summary>
/// One attempt to authorize an order's total. It is saved as Authorizing BEFORE the provider is called, so a crash
/// never loses a possible hold: the attempt can always be looked up by its reference. Status changes only through
/// <see cref="Resolve"/>, each appended to the attempt's history with its actor and correlation id.
/// </summary>
internal sealed class PaymentAttempt
{
    public const int MaxCustomerIdLength = 128;

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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Incremented by every change, so the rowversion check always runs.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<PaymentAttemptEvent> Events => _events;

    /// <summary>Our reference at the provider: this attempt's id, without dashes.</summary>
    public string Reference => Id.ToString("N");

    /// <summary>
    /// Statuses in which the attempt holds, or may hold, funds: at most one such attempt per order. Every one of them
    /// needs a way out before checkout is exposed: a lookup (the open ones), a void (Authorized), or a person (ManualReview).
    /// </summary>
    public static IReadOnlyList<PaymentAttemptStatus> LiveStatuses { get; } =
    [
        PaymentAttemptStatus.Authorizing, PaymentAttemptStatus.ActionRequired, PaymentAttemptStatus.AuthorizationUnknown,
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.ManualReview,
    ];

    public bool IsFinal => Status is not (PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.AuthorizationUnknown);

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
    /// Records what the provider established. The same status again only fills in provider details, without a history
    /// entry. A final attempt never changes, and nothing goes back to Authorizing.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> Resolve(
        PaymentAttemptStatus to, string reason, PaymentChange change, string? providerId = null, string? providerPaymentId = null, string? declineReason = null)
    {
        if (IsFinal)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Failure(PaymentAttemptTransitionError.AlreadyFinal);
        }

        if (to == PaymentAttemptStatus.Authorizing)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Failure(PaymentAttemptTransitionError.Illegal);
        }

        if (providerId is not null && providerPaymentId is not null)
        {
            ProviderId = providerId;
            ProviderPaymentId = providerPaymentId;
        }

        DeclineReason = declineReason ?? DeclineReason;
        if (to == Status)
        {
            UpdatedAt = change.At;
            Revision++;
        }
        else
        {
            var from = Status;
            Status = to;
            Record(from, reason, change, providerPaymentId);
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
