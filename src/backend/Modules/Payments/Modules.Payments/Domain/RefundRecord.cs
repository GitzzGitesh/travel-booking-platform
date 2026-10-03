using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Domain;

/// <summary>Where one refund stands (ADR 0027).</summary>
internal enum RefundRecordStatus
{
    /// <summary>Approved by Orders; not yet sent to the provider.</summary>
    Requested,

    /// <summary>Saved before the provider is called: a crash leaves a refund to look up, never one sent twice.</summary>
    Refunding,

    /// <summary>The provider accepted it; its final outcome comes later (looked up).</summary>
    Pending,

    /// <summary>The outcome is unknown (a timeout, an error after sending): looked up by our key, never sent again.</summary>
    Unknown,

    Succeeded,

    /// <summary>Nothing was refunded: the provider could not, or the refund was not possible (it exceeded what was captured).</summary>
    Failed,

    /// <summary>Still unknown after its limit, or not as expected: a person decides (alert).</summary>
    ManualReview,
}

/// <summary>
/// One refund of a captured payment (ADR 0006, ADR 0027): identified by Orders' refund id, which also makes its provider
/// key (<see cref="Key"/>), so the provider refunds at most once whatever happens here. The total of refunds that are
/// not failed never exceeds the captured amount (checked when it is recorded).
/// </summary>
internal sealed class RefundRecord
{
    public const int MaxReasonLength = 300;

    /// <summary>A refund whose outcome is still unknown after this goes to a person.</summary>
    public static readonly TimeSpan UnresolvedAfter = TimeSpan.FromHours(24);

    /// <summary>A refund left in <see cref="RefundRecordStatus.Refunding"/> longer than this was interrupted: it is looked up.</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(5);

    private RefundRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid AttemptId { get; private set; }

    public Guid OrderId { get; private set; }

    public decimal AmountValue { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public Money Amount => new(AmountValue, new CurrencyCode(CurrencyCode));

    public RefundRecordStatus Status { get; private set; }

    public string? ProviderRefundId { get; private set; }

    public string? Reason { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string Key => $"{Id:N}:refund";

    public bool IsFinal => Status is RefundRecordStatus.Succeeded or RefundRecordStatus.Failed;

    /// <summary>Counts against what can still be refunded: every refund except a failed one.</summary>
    public bool CountsAsRefunded => Status is not RefundRecordStatus.Failed;

    public static RefundRecord Requested(Guid refundId, Guid attemptId, Guid orderId, Money amount, string? correlationId, DateTimeOffset at) => new()
    {
        Id = refundId,
        AttemptId = attemptId,
        OrderId = orderId,
        AmountValue = amount.Amount,
        CurrencyCode = amount.Currency.Value,
        Status = RefundRecordStatus.Requested,
        CorrelationId = correlationId,
        RequestedAt = at,
        UpdatedAt = at,
    };

    /// <summary>Recorded as not possible (it would exceed what can be refunded): nothing is sent.</summary>
    public static RefundRecord NotPossible(Guid refundId, Guid attemptId, Guid orderId, Money amount, string reason, string? correlationId, DateTimeOffset at)
    {
        var record = Requested(refundId, attemptId, orderId, amount, correlationId, at);
        record.Finish(RefundRecordStatus.Failed, reason, at);
        return record;
    }

    public bool BeginRefund(DateTimeOffset at)
    {
        if (Status is not RefundRecordStatus.Requested)
        {
            return false;
        }

        Status = RefundRecordStatus.Refunding;
        UpdatedAt = at;
        return true;
    }

    /// <summary>Records what the provider said; a final outcome is settled once (true when it became final now).</summary>
    public bool Record(RefundRecordStatus status, string reason, DateTimeOffset at, string? providerRefundId = null)
    {
        if (IsFinal || status is RefundRecordStatus.Requested or RefundRecordStatus.Refunding)
        {
            return false;
        }

        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        if (status is RefundRecordStatus.Succeeded or RefundRecordStatus.Failed)
        {
            Finish(status, reason, at);
            return true;
        }

        Status = status;
        Reason = Bounded(reason);
        UpdatedAt = at;
        return false;
    }

    /// <summary>Due for a lookup: pending, unknown, or interrupted while refunding.</summary>
    public bool NeedsLookup(DateTimeOffset now) =>
        Status is RefundRecordStatus.Pending or RefundRecordStatus.Unknown
        || (Status is RefundRecordStatus.Refunding && now - UpdatedAt >= InterruptedAfter);

    private void Finish(RefundRecordStatus status, string reason, DateTimeOffset at)
    {
        Status = status;
        Reason = Bounded(reason);
        UpdatedAt = at;
        SettledAt = at;
    }

    private static string Bounded(string value) => value.Length <= MaxReasonLength ? value : value[..MaxReasonLength];
}
