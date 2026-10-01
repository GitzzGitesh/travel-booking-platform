using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Access.Domain;

namespace TravelBooking.Modules.Access.Application;

internal interface IRoleGrantStore
{
    Task<RoleChangeRequest?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Newest first; <paramref name="before"/> is the cursor (the last request's time and id).</summary>
    Task<IReadOnlyList<RoleChangeRequest>> FindRequestsAsync(RoleChangeStatus status, (DateTimeOffset RequestedAt, Guid Id)? before, int limit, CancellationToken cancellationToken);

    Task<RoleGrant?> FindActiveGrantAsync(string objectId, string role, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleGrant>> FindActiveGrantsAsync(CancellationToken cancellationToken);

    /// <summary>The roles this account holds through approved grants: read on every staff sign-in, so a revocation takes effect at once.</summary>
    Task<IReadOnlyList<string>> FindActiveRolesAsync(string objectId, CancellationToken cancellationToken);

    void Add(RoleChangeRequest request);

    void Add(RoleGrant grant);

    void Audit(AuditEntry entry);

    /// <summary>Saves; false on a concurrent change or a unique-constraint conflict (one pending request, one active grant per account and role).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>The staff member acting: our staff id and their own workforce account (for the maker-checker rules).</summary>
internal sealed record StaffActor(string StaffId, string ObjectId)
{
    public string Audited => $"staff:{StaffId}";
}

internal enum RoleChangeFailure
{
    InvalidRequest,
    NotFound,

    /// <summary>A grant of a role the account holds already, or a revocation of one it does not hold.</summary>
    NothingToChange,

    /// <summary>A request for this account and role is already waiting for a decision.</summary>
    PendingExists,

    /// <summary>Nobody requests a change to their own access, nor approves their own request or a change to their own access.</summary>
    SelfApproval,

    /// <summary>A revocation is withdrawn only by its requester.</summary>
    WithdrawalOnly,

    /// <summary>The role comes from configuration (the bootstrap): it is granted or removed there, not here.</summary>
    GrantedByConfiguration,

    /// <summary>The requester no longer holds the permission to request: the request cannot be approved.</summary>
    RequesterNoLongerAuthorized,

    /// <summary>Pending longer than the approval window: request again.</summary>
    Expired,

    AlreadyDecided,

    /// <summary>Changed at the same time: nothing was saved; try again.</summary>
    Conflict,
}

/// <summary>
/// Managed role grants with maker-checker (ADR 0022, security rules: permission grants are high-risk). A request names the
/// account (its workforce object id), the role and a reason; a different staff member approves or rejects it. Only an
/// approval changes access: a grant adds an active role, a revocation ends one. Every request and decision is audited in
/// the Access schema in the same save. The configuration grants (<c>Access:RoleAssignments</c>) remain the bootstrap.
/// </summary>
internal sealed partial class RoleChangeHandler(
    IRoleGrantStore store, IOptionsMonitor<AccessOptions> configuration, TimeProvider timeProvider, ILogger<RoleChangeHandler> logger)
{
    public async Task<Result<RoleChangeRequest, RoleChangeFailure>> RequestAsync(
        string objectId, string role, RoleChangeKind kind, string reason, StaffActor requester, AuditSource source, CancellationToken cancellationToken)
    {
        if (!RoleChangeRequest.IsCanonicalObjectId(objectId) || !StaffRoles.Permissions.ContainsKey(role) || !Enum.IsDefined(kind) || !AuditReasons.IsValid(reason))
        {
            return Failure<RoleChangeRequest>(RoleChangeFailure.InvalidRequest);
        }

        if (string.Equals(objectId, requester.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            LogRefused(logger, requester.StaffId, "self-request", Guid.Empty);
            return Failure<RoleChangeRequest>(RoleChangeFailure.SelfApproval); // never a request about one's own access
        }

        if (IsFromConfiguration(objectId, role))
        {
            return Failure<RoleChangeRequest>(RoleChangeFailure.GrantedByConfiguration);
        }

        var active = await store.FindActiveGrantAsync(objectId, role, cancellationToken);
        if ((kind is RoleChangeKind.Grant) == (active is not null))
        {
            return Failure<RoleChangeRequest>(RoleChangeFailure.NothingToChange);
        }

        var now = timeProvider.GetUtcNow();
        var request = RoleChangeRequest.Create(objectId, role, kind, reason, requester.StaffId, requester.ObjectId, now);
        store.Add(request);
        store.Audit(AuditEntry.For(source, now, requester.Audited, "access.role-change.request", $"role-change:{request.Id}", null, $"{kind} {role} for {objectId}; {reason}"));
        return await store.TrySaveAsync(cancellationToken)
            ? Result<RoleChangeRequest, RoleChangeFailure>.Success(request)
            : Failure<RoleChangeRequest>(RoleChangeFailure.PendingExists);
    }

    public async Task<Result<RoleChangeRequest, RoleChangeFailure>> DecideAsync(
        Guid requestId, bool approve, string reason, StaffActor decider, AuditSource source, CancellationToken cancellationToken)
    {
        if (!AuditReasons.IsValid(reason))
        {
            return Failure<RoleChangeRequest>(RoleChangeFailure.InvalidRequest);
        }

        if (await store.FindRequestAsync(requestId, cancellationToken) is not { } request)
        {
            return Failure<RoleChangeRequest>(RoleChangeFailure.NotFound);
        }

        var now = timeProvider.GetUtcNow();
        RoleGrant? active = null;
        if (approve && request.Status is RoleChangeStatus.Pending)
        {
            // The requester must still be allowed to request (a revoked or departed administrator's queue is void), and
            // the change must still change something.
            if (!await StillAllowedToRequestAsync(request.RequestedByObjectId, cancellationToken))
            {
                return Failure<RoleChangeRequest>(RoleChangeFailure.RequesterNoLongerAuthorized);
            }

            active = await store.FindActiveGrantAsync(request.ObjectId, request.Role, cancellationToken);
            if ((request.Kind is RoleChangeKind.Grant) == (active is not null))
            {
                return Failure<RoleChangeRequest>(RoleChangeFailure.NothingToChange);
            }
        }

        var decided = approve ? request.Approve(decider.StaffId, decider.ObjectId, reason, now) : request.Reject(decider.StaffId, decider.ObjectId, reason, now);
        if (!decided.IsSuccess)
        {
            if (decided.Error is RoleChangeError.SelfApproval or RoleChangeError.WithdrawalOnly)
            {
                LogRefused(logger, decider.StaffId, decided.Error.ToString(), request.Id); // a maker-checker refusal is a security event
            }

            return Failure<RoleChangeRequest>(decided.Error switch
            {
                RoleChangeError.SelfApproval => RoleChangeFailure.SelfApproval,
                RoleChangeError.WithdrawalOnly => RoleChangeFailure.WithdrawalOnly,
                RoleChangeError.Expired => RoleChangeFailure.Expired,
                _ => RoleChangeFailure.AlreadyDecided,
            });
        }

        if (approve)
        {
            if (request.Kind is RoleChangeKind.Grant)
            {
                store.Add(RoleGrant.From(request, now));
            }
            else
            {
                active!.Revoke(request.Id, now);
            }
        }

        store.Audit(AuditEntry.For(source, now, decider.Audited, approve ? "access.role-change.approve" : "access.role-change.reject",
            $"role-change:{request.Id}", nameof(RoleChangeStatus.Pending), $"{request.Status}: {request.Kind} {request.Role} for {request.ObjectId}; {reason}"));
        return await store.TrySaveAsync(cancellationToken)
            ? Result<RoleChangeRequest, RoleChangeFailure>.Success(request)
            : Failure<RoleChangeRequest>(RoleChangeFailure.Conflict);
    }

    private bool IsFromConfiguration(string objectId, string role) =>
        configuration.CurrentValue.RoleAssignments.Any(a => string.Equals(a.ObjectId, objectId, StringComparison.Ordinal) && a.Roles.Contains(role, StringComparer.Ordinal));

    private async Task<bool> StillAllowedToRequestAsync(string objectId, CancellationToken cancellationToken) =>
        configuration.CurrentValue.PermissionsFor(objectId)
            .Union(StaffRoles.PermissionsOf(await store.FindActiveRolesAsync(objectId, cancellationToken)), StringComparer.Ordinal)
            .Contains(Contracts.StaffPermissions.AccessGrantsRequest, StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "RoleChangeRefused", Message = "Security: staff member {StaffId} was refused a role change ({Rule}; request {RequestId})")]
    private static partial void LogRefused(ILogger logger, string staffId, string rule, Guid requestId);

    private static Result<T, RoleChangeFailure> Failure<T>(RoleChangeFailure failure)
        where T : notnull => Result<T, RoleChangeFailure>.Failure(failure);
}
