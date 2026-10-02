namespace TravelBooking.Modules.Customers.Domain;

internal enum LegalHoldReleaseStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>Why a release decision was refused (ADR 0026).</summary>
internal enum LegalHoldReleaseRefusal
{
    /// <summary>The approver is the requester (by staff id or by workforce account).</summary>
    SelfApproval,

    /// <summary>The request was already approved or rejected (a repeat changes nothing).</summary>
    AlreadyDecided,

    /// <summary>The request waited longer than the approval window: it can only be rejected (withdrawn).</summary>
    Expired,
}

/// <summary>
/// A request to release a legal hold (ADR 0026, maker-checker): the hold stays in force while it waits. A different staff
/// member with the approve permission decides; nobody approves their own request (compared by staff id and by workforce
/// account). A request waits at most <see cref="ApprovalWindow"/>; after that it can only be rejected.
/// </summary>
internal sealed class LegalHoldReleaseRequest
{
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(7);

    private LegalHoldReleaseRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public LegalHoldReleaseStatus Status { get; private set; }

    public string RequestedBy { get; private set; } = string.Empty;

    public string RequestedByAccount { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string? DecidedBy { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static LegalHoldReleaseRequest For(Guid orderId, string staffId, string account, string reason, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = orderId,
        Status = LegalHoldReleaseStatus.Pending,
        RequestedBy = staffId,
        RequestedByAccount = account,
        Reason = reason,
        RequestedAt = at,
    };

    public bool IsRequester(string staffId, string account) =>
        string.Equals(RequestedBy, staffId, StringComparison.Ordinal) || string.Equals(RequestedByAccount, account, StringComparison.OrdinalIgnoreCase);

    /// <summary>A different person approves, within the window. Null on success.</summary>
    public LegalHoldReleaseRefusal? Approve(string staffId, string account, string reason, DateTimeOffset at)
    {
        if (Status is not LegalHoldReleaseStatus.Pending)
        {
            return LegalHoldReleaseRefusal.AlreadyDecided;
        }

        if (IsRequester(staffId, account))
        {
            return LegalHoldReleaseRefusal.SelfApproval;
        }

        if (at - RequestedAt > ApprovalWindow)
        {
            return LegalHoldReleaseRefusal.Expired;
        }

        Decide(LegalHoldReleaseStatus.Approved, staffId, reason, at);
        return null;
    }

    /// <summary>A checker rejects it, or its requester withdraws it (at any time while pending). Null on success.</summary>
    /// <remarks>Withdrawal is limited to the requester by the caller; rejecting needs the approve permission.</remarks>
    public LegalHoldReleaseRefusal? Reject(string staffId, string reason, DateTimeOffset at)
    {
        if (Status is not LegalHoldReleaseStatus.Pending)
        {
            return LegalHoldReleaseRefusal.AlreadyDecided;
        }

        Decide(LegalHoldReleaseStatus.Rejected, staffId, reason, at);
        return null;
    }

    private void Decide(LegalHoldReleaseStatus status, string staffId, string reason, DateTimeOffset at)
    {
        Status = status;
        DecidedBy = staffId;
        DecisionReason = reason;
        DecidedAt = at;
    }
}
