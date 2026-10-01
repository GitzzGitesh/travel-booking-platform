using System.Security.Claims;

namespace TravelBooking.BuildingBlocks.Http;

/// <summary>
/// The authenticated staff member, as every module sees it (ADR 0008, ADR 0022). The Access module validates the
/// workforce identity provider's token (with MFA) and adds our internal staff id and the member's permissions in a
/// separate identity (<see cref="MappedIdentityType"/>), which only it creates. Admin endpoints require a permission
/// policy (<see cref="PolicyFor"/>) for a permission the Access module publishes (<c>Modules.Access.Contracts</c>); code
/// never checks role names.
/// </summary>
public static class StaffIdentity
{
    /// <summary>The authentication scheme for staff tokens (Entra ID workforce tenant). Customers have their own.</summary>
    public const string Scheme = "Staff";

    /// <summary>Our internal staff id. Reserved: a token carrying it is refused.</summary>
    public const string StaffIdClaim = "tb_staff_id";

    /// <summary>One claim per granted permission. Reserved: a token carrying it is refused.</summary>
    public const string PermissionClaim = "tb_permission";

    /// <summary>The authentication type of the identity holding our claims, added by the Access module only.</summary>
    public const string MappedIdentityType = "tb-staff";

    /// <summary>The authorization policy that requires a signed-in staff member (with MFA) holding this permission.</summary>
    public static string PolicyFor(string permission) => $"staff:{permission}";

    /// <summary>The internal staff id; null for anyone who is not an authenticated, mapped staff member.</summary>
    public static string? StaffId(this ClaimsPrincipal user) => Mapped(user)?.Claims.SingleOrDefault(c => c.Type == StaffIdClaim)?.Value;

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        Mapped(user)?.Claims.Any(c => c.Type == PermissionClaim && c.Value == permission) == true;

    private static ClaimsIdentity? Mapped(ClaimsPrincipal user)
    {
        var mapped = user.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == MappedIdentityType).ToList();
        return mapped.Count == 1 && user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == Scheme) ? mapped[0] : null;
    }
}
