using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Orders.Domain;

internal enum RefundCaseStatus
{
    /// <summary>Waiting for a second person (ADR 0027).</summary>
    PendingApproval,

    /// <summary>Approved: Payments was asked to refund it (outbox).</summary>
    Approved,

    Rejected,

    Refunded,

    /// <summary>The provider could not return the money, or the refund was not possible: a person follows it up.</summary>
    RefundFailed,

    /// <summary>A cancellation with nothing to refund (the supplier refunded nothing, or the fee covers it).</summary>
    NoRefund,
}

internal enum RefundCaseRefusal
{
    SelfApproval,
    AlreadyDecided,
    Expired,
}

/// <summary>
/// One refund of an order (ADR 0027): its amount is computed by the server when the case is opened and never exceeds what
/// can still be refunded. It is decided by a different person than the one who opened it (compared by staff id and by
/// workforce account), unless the policy lets it through without a second person. Only then does Payments refund it, once,
/// under the case's id.
/// </summary>
internal sealed class RefundCase
{
    public const int MaxTextLength = 200;
    public const int MaxKeyLength = 100;
    public const int MaxFingerprintLength = 1200;

    /// <summary>A case waits at most this long for approval; after that it can only be rejected (open a new one).</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(7);

    private RefundCase()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid PaymentId { get; private set; }

    public RefundCaseKind Kind { get; private set; }

    /// <summary>The cancelled items (comma-separated ids); empty for a goodwill refund.</summary>
    public string ItemIds { get; private set; } = string.Empty;

    public decimal AmountValue { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public Money Amount => new(AmountValue, new CurrencyCode(CurrencyCode));

    /// <summary>What the supplier refunds us for the cancelled items, as its desk stated (null for goodwill).</summary>
    public decimal? SupplierRefundValue { get; private set; }

    public decimal FeeValue { get; private set; }

    public string? SupplierReference { get; private set; }

    public RefundCaseStatus Status { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string RequestedBy { get; private set; } = string.Empty;

    public string RequestedByAccount { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string? DecidedBy { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>The requester's idempotency key: unique per requester, so a repeated request returns this case.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>What was asked for under the key: the same key with other details is refused.</summary>
    public string RequestFingerprint { get; private set; } = string.Empty;

    /// <summary>When operations were warned that this approved refund is past its target (once).</summary>
    public DateTimeOffset? OverdueAlertedAt { get; private set; }

    public IReadOnlyList<Guid> CancelledItemIds => [.. ItemIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse)];

    public bool IsOpen => Status is RefundCaseStatus.PendingApproval or RefundCaseStatus.Approved;

    /// <summary>Still holds back money that could otherwise be refunded: approved, or waiting within its approval window.</summary>
    public bool HoldsRefundable(DateTimeOffset now) =>
        Status is RefundCaseStatus.Approved || (Status is RefundCaseStatus.PendingApproval && now - RequestedAt <= ApprovalWindow);

    public void MarkOverdueAlerted(DateTimeOffset at) => OverdueAlertedAt ??= at;

    public static RefundCase Open(
        Guid orderId, Guid paymentId, RefundCaseKind kind, IReadOnlyCollection<Guid> itemIds, Money amount, Money? supplierRefund, Money fee,
        string? supplierReference, string reason, string staffId, string account, string idempotencyKey, string fingerprint, DateTimeOffset at) => new()
        {
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprint,
            Id = Guid.NewGuid(),
            OrderId = orderId,
            PaymentId = paymentId,
            Kind = kind,
            ItemIds = string.Join(',', itemIds.Select(i => i.ToString("D"))),
            AmountValue = amount.Amount,
            CurrencyCode = amount.Currency.Value,
            SupplierRefundValue = supplierRefund?.Amount,
            FeeValue = fee.Amount,
            SupplierReference = supplierReference,
            Status = amount.Amount > 0 ? RefundCaseStatus.PendingApproval : RefundCaseStatus.NoRefund,
            Reason = reason,
            RequestedBy = staffId,
            RequestedByAccount = account,
            RequestedAt = at,
            SettledAt = amount.Amount > 0 ? null : at,
        };

    public bool IsRequester(string staffId, string account) =>
        string.Equals(RequestedBy, staffId, StringComparison.Ordinal) || string.Equals(RequestedByAccount, account, StringComparison.OrdinalIgnoreCase);

    /// <summary>A different person approves, within the window. Null on success.</summary>
    public RefundCaseRefusal? Approve(string staffId, string account, string reason, DateTimeOffset at)
    {
        if (Status is not RefundCaseStatus.PendingApproval)
        {
            return RefundCaseRefusal.AlreadyDecided;
        }

        if (IsRequester(staffId, account))
        {
            return RefundCaseRefusal.SelfApproval;
        }

        if (at - RequestedAt > ApprovalWindow)
        {
            return RefundCaseRefusal.Expired;
        }

        Decide(RefundCaseStatus.Approved, staffId, reason, at);
        return null;
    }

    /// <summary>A checker rejects it, or its requester withdraws it. Null on success.</summary>
    public RefundCaseRefusal? Reject(string staffId, string reason, DateTimeOffset at)
    {
        if (Status is not RefundCaseStatus.PendingApproval)
        {
            return RefundCaseRefusal.AlreadyDecided;
        }

        Decide(RefundCaseStatus.Rejected, staffId, reason, at);
        return null;
    }

    /// <summary>Payments' final outcome; true when it changed the case (a redelivered outcome changes nothing).</summary>
    public bool Settle(bool refunded, DateTimeOffset at)
    {
        if (Status is not RefundCaseStatus.Approved)
        {
            return false;
        }

        Status = refunded ? RefundCaseStatus.Refunded : RefundCaseStatus.RefundFailed;
        SettledAt = at;
        return true;
    }

    private void Decide(RefundCaseStatus status, string staffId, string reason, DateTimeOffset at)
    {
        Status = status;
        DecidedBy = staffId;
        DecisionReason = reason;
        DecidedAt = at;
    }
}
