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

/// <summary>
/// One attempt to authorize an order's total. It is saved as Authorizing BEFORE the provider is called, so a crash
/// never loses a possible hold: the attempt can always be looked up by its reference. Status changes only through
/// <see cref="Resolve"/>, each appended to the attempt's history.
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

    /// <summary>Statuses in which the attempt holds, or may hold, funds: at most one such attempt per order.</summary>
    public static IReadOnlyList<PaymentAttemptStatus> LiveStatuses { get; } =
    [
        PaymentAttemptStatus.Authorizing, PaymentAttemptStatus.ActionRequired, PaymentAttemptStatus.AuthorizationUnknown,
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.ManualReview,
    ];

    public bool IsFinal => Status is not (PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.AuthorizationUnknown);

    public static PaymentAttempt Start(Guid orderId, string customerId, string idempotencyKey, Money amount, DateTimeOffset now)
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
            CreatedAt = now,
            UpdatedAt = now,
        };
        attempt.Record(null, "Authorization requested", now, null);
        return attempt;
    }

    /// <summary>
    /// Records what the provider established. Returns false, changing nothing, when the attempt is already final or the
    /// target is Authorizing (a late or duplicate outcome). The same status again only fills in provider details.
    /// </summary>
    public bool Resolve(PaymentAttemptStatus to, string reason, DateTimeOffset now, string? providerId = null, string? providerPaymentId = null, string? declineReason = null)
    {
        if (IsFinal || to == PaymentAttemptStatus.Authorizing)
        {
            return false;
        }

        if (providerId is not null && providerPaymentId is not null)
        {
            ProviderId = providerId;
            ProviderPaymentId = providerPaymentId;
        }

        DeclineReason = declineReason ?? DeclineReason;
        if (to == Status)
        {
            UpdatedAt = now;
            Revision++;
            return true;
        }

        var from = Status;
        Status = to;
        Record(from, reason, now, providerPaymentId);
        return true;
    }

    private void Record(PaymentAttemptStatus? from, string reason, DateTimeOffset at, string? providerReference)
    {
        _events.Add(new PaymentAttemptEvent(Id, at, from?.ToString(), Status.ToString(), reason, providerReference));
        UpdatedAt = at;
        Revision++;
    }
}

/// <summary>An append-only record of one attempt transition (payments are audited). Never updated or deleted.</summary>
internal sealed record PaymentAttemptEvent(Guid PaymentAttemptId, DateTimeOffset At, string? FromStatus, string ToStatus, string Reason, string? ProviderReference)
{
    public long Id { get; private set; }
}
