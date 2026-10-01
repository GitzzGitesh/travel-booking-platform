using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Access.Domain;

internal enum RoleChangeKind
{
    Grant,
    Revoke,
}

internal enum RoleChangeStatus
{
    Pending,
    Approved,
    Rejected,
}

internal enum RoleChangeError
{
    /// <summary>Already approved or rejected: nothing changes (a repeat, or decided meanwhile).</summary>
    AlreadyDecided,

    /// <summary>Maker-checker: nobody approves their own request, nor decides about their own access (either way).</summary>
    SelfApproval,

    /// <summary>A revocation is withdrawn only by its requester: nobody else can stop a removal of access.</summary>
    WithdrawalOnly,

    /// <summary>Pending too long to be approved (it may no longer reflect intent): request again.</summary>
    Expired,
}

/// <summary>
/// A request to grant or revoke a role for a workforce account (ADR 0022: managed grants with maker-checker). One staff
/// member requests it with a reason; a different one approves or rejects it. Only an approval changes access. Every
/// step is audited by the handler, in the same save.
/// </summary>
internal sealed class RoleChangeRequest
{
    /// <summary>A request older than this cannot be approved (it can still be rejected).</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(7);

    /// <summary>A workforce object id as Entra issues it: a lowercase GUID (so an id can never match another by case).</summary>
    public static bool IsCanonicalObjectId(string? objectId) =>
        objectId is { Length: 36 } && Guid.TryParseExact(objectId, "D", out var parsed) && parsed.ToString("D") == objectId;

    private RoleChangeRequest()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The workforce account (Entra object id) whose access changes.</summary>
    public string ObjectId { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    public RoleChangeKind Kind { get; private set; }

    public RoleChangeStatus Status { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string RequestedBy { get; private set; } = string.Empty;

    /// <summary>The requester's own account: they may not request a change to their own access.</summary>
    public string RequestedByObjectId { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionReason { get; private set; }

    /// <summary>Incremented by every change, so the rowversion check always runs.</summary>
    public int Revision { get; private set; }

    public static RoleChangeRequest Create(string objectId, string role, RoleChangeKind kind, string reason, string requestedBy, string requestedByObjectId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ObjectId = objectId,
        Role = role,
        Kind = kind,
        Status = RoleChangeStatus.Pending,
        Reason = reason,
        RequestedBy = requestedBy,
        RequestedByObjectId = requestedByObjectId,
        RequestedAt = at,
    };

    /// <summary>
    /// A different staff member approves: never the requester (by staff id or by account), never the account whose access
    /// changes, and never after <see cref="ApprovalWindow"/>.
    /// </summary>
    public Result<RoleChangeStatus, RoleChangeError> Approve(string approverStaffId, string approverObjectId, string reason, DateTimeOffset at)
    {
        if (Status is not RoleChangeStatus.Pending)
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.AlreadyDecided);
        }

        if (string.Equals(approverStaffId, RequestedBy, StringComparison.Ordinal)
            || string.Equals(approverObjectId, RequestedByObjectId, StringComparison.Ordinal)
            || string.Equals(approverObjectId, ObjectId, StringComparison.Ordinal))
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.SelfApproval);
        }

        if (at - RequestedAt > ApprovalWindow)
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.Expired);
        }

        return Decide(RoleChangeStatus.Approved, approverStaffId, reason, at);
    }

    /// <summary>
    /// Rejecting never adds access, so a grant may be rejected by any staff member allowed to decide, except the account
    /// it concerns. A revocation may only be withdrawn by its requester: nobody, least of all its target, can veto a removal.
    /// </summary>
    public Result<RoleChangeStatus, RoleChangeError> Reject(string staffId, string objectId, string reason, DateTimeOffset at)
    {
        if (Status is not RoleChangeStatus.Pending)
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.AlreadyDecided);
        }

        if (string.Equals(objectId, ObjectId, StringComparison.Ordinal))
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.SelfApproval);
        }

        if (Kind is RoleChangeKind.Revoke && !string.Equals(staffId, RequestedBy, StringComparison.Ordinal))
        {
            return Result<RoleChangeStatus, RoleChangeError>.Failure(RoleChangeError.WithdrawalOnly);
        }

        return Decide(RoleChangeStatus.Rejected, staffId, reason, at);
    }

    private Result<RoleChangeStatus, RoleChangeError> Decide(RoleChangeStatus status, string staffId, string reason, DateTimeOffset at)
    {
        Status = status;
        DecidedBy = staffId;
        DecidedAt = at;
        DecisionReason = reason;
        Revision++;
        return Result<RoleChangeStatus, RoleChangeError>.Success(status);
    }
}

/// <summary>A role a workforce account holds through an approved request, until an approved revocation ends it.</summary>
internal sealed class RoleGrant
{
    private RoleGrant()
    {
    }

    public Guid Id { get; private set; }

    public string ObjectId { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; private set; }

    public Guid GrantedByRequestId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedByRequestId { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static RoleGrant From(RoleChangeRequest approved, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ObjectId = approved.ObjectId,
        Role = approved.Role,
        GrantedAt = at,
        GrantedByRequestId = approved.Id,
    };

    public void Revoke(Guid requestId, DateTimeOffset at)
    {
        RevokedAt = at;
        RevokedByRequestId = requestId;
    }
}
