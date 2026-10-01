using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Access.Domain;

namespace TravelBooking.Modules.Access.Application;

/// <summary>
/// The roles: named bundles of permissions (ADR 0008). Code checks permissions, never role names. A role is granted
/// only through <see cref="AccessOptions.RoleAssignments"/> for now (ADR 0022); managed grants, with maker-checker and
/// audit, come with the access administration screens.
/// </summary>
internal static class StaffRoles
{
    public const string Operations = "Operations";
    public const string Administrator = "Administrator";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Permissions { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Operations] = [StaffPermissions.OrdersRead, StaffPermissions.BookingsReviewResolve],
        [Administrator] = StaffPermissions.All,
    };

    public static IReadOnlyList<string> PermissionsOf(IEnumerable<string> roles) =>
        [.. roles.Where(Permissions.ContainsKey).SelectMany(role => Permissions[role]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// <c>Access</c>: which staff accounts (by the workforce tenant's object id, not a secret) hold which roles, per
/// environment. Nobody holds any role by default: an unassigned staff member signs in but may do nothing (403).
/// </summary>
internal sealed class AccessOptions
{
    public const string SectionName = "Access";

    public List<RoleAssignment> RoleAssignments { get; set; } = [];

    public bool IsValid() =>
        RoleAssignments.All(a => StaffMember.IsValidIdentity("issuer", a.ObjectId) && a.Roles.Count > 0 && a.Roles.All(StaffRoles.Permissions.ContainsKey))
        && RoleAssignments.Select(a => a.ObjectId).Distinct(StringComparer.Ordinal).Count() == RoleAssignments.Count;

    public IReadOnlyList<string> PermissionsFor(string objectId) =>
        StaffRoles.PermissionsOf(RoleAssignments.Where(a => string.Equals(a.ObjectId, objectId, StringComparison.Ordinal)).SelectMany(a => a.Roles));
}

internal sealed class RoleAssignment
{
    public string ObjectId { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = [];
}

internal interface IStaffStore
{
    Task<StaffMember?> FindByIdentityAsync(string issuer, string objectId, CancellationToken cancellationToken);

    /// <summary>Adds the member; false if this account was added at the same time (unique constraint).</summary>
    Task<bool> TryAddAsync(StaffMember member, CancellationToken cancellationToken);
}

/// <summary>Maps a validated workforce account to its internal staff id, creating it on first sign-in (safe under concurrency).</summary>
internal sealed class StaffDirectory(IStaffStore store, TimeProvider timeProvider)
{
    public async Task<string?> ResolveAsync(string? issuer, string? objectId, CancellationToken cancellationToken)
    {
        if (!StaffMember.IsValidIdentity(issuer, objectId))
        {
            return null;
        }

        if (await store.FindByIdentityAsync(issuer!, objectId!, cancellationToken) is { } existing)
        {
            return existing.StaffId;
        }

        var member = StaffMember.For(issuer!, objectId!, timeProvider.GetUtcNow());
        if (await store.TryAddAsync(member, cancellationToken))
        {
            return member.StaffId;
        }

        return (await store.FindByIdentityAsync(issuer!, objectId!, cancellationToken))?.StaffId
            ?? throw new InvalidOperationException("A staff insert conflicted, but no staff member has this identity.");
    }
}
