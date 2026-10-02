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

    /// <summary>The admin-web session cookie (ADR 0023): the same staff mapping as <see cref="Scheme"/>, re-read on every request.</summary>
    public const string SessionScheme = "StaffSession";

    /// <summary>Required on every unsafe request authenticated by the session cookie (ADR 0023: CSRF).</summary>
    public const string CsrfHeader = "X-TB-Staff-Csrf";

    /// <summary>The policy that requires a signed-in, mapped staff member, whatever their permissions.</summary>
    public const string SignedInPolicy = "staff:signed-in";

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

    /// <summary>The mapped staff member's permissions; empty for anyone else.</summary>
    public static IReadOnlyList<string> Permissions(this ClaimsPrincipal user) =>
        Mapped(user) is { } mapped ? [.. mapped.Claims.Where(c => c.Type == PermissionClaim).Select(c => c.Value).Order(StringComparer.Ordinal)] : [];

    /// <summary>The workforce account (object id) of the mapped staff member, from the validated token or session.</summary>
    public static string? AccountObjectId(this ClaimsPrincipal user) =>
        Mapped(user) is null ? null : Signed(user).SingleOrDefault()?.FindFirst("oid")?.Value;

    // Exactly one staff identity (a token or a session, never both) and exactly one mapping, or nobody.
    private static ClaimsIdentity? Mapped(ClaimsPrincipal user)
    {
        var mapped = user.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == MappedIdentityType).ToList();
        return mapped.Count == 1 && Signed(user).Count == 1 ? mapped[0] : null;
    }

    private static List<ClaimsIdentity> Signed(ClaimsPrincipal user) =>
        [.. user.Identities.Where(i => i.IsAuthenticated && (i.AuthenticationType == Scheme || i.AuthenticationType == SessionScheme))];
}
