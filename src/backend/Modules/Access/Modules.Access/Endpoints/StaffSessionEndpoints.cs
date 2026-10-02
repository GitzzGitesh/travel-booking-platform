using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Application;
using TravelBooking.Modules.Access.Domain;

namespace TravelBooking.Modules.Access.Endpoints;

/// <summary>Development only: sign in as this workforce object id (a lowercase GUID), without a tenant.</summary>
public sealed class DevelopmentSignInRequest
{
    [Required]
    [StringLength(36, MinimumLength = 36)]
    public string? ObjectId { get; init; }
}

/// <summary>
/// The admin-web session (ADR 0023). Thin: sign-in is the OpenID Connect handler's, and the signed-in staff member comes
/// from the validated session or token only.
/// </summary>
internal static class StaffSessionEndpoints
{
    public const int MaxReturnUrlLength = 2048;

    // For the UI only (what to show); the server checks every call itself.
    public static Ok<StaffSessionResponse> Current(ClaimsPrincipal user) =>
        TypedResults.Ok(new StaffSessionResponse($"staff:{user.StaffId()}", user.Permissions()));

    public static async Task<Results<ChallengeHttpResult, ProblemHttpResult>> SignIn(IAuthenticationSchemeProvider schemes, string? returnUrl = null)
    {
        if (await schemes.GetSchemeAsync(StaffSessions.SignInScheme) is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: "staff-sign-in-unavailable",
                title: "Staff sign-in is not configured in this environment.");
        }

        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = IsLocal(returnUrl) ? returnUrl : "/" }, [StaffSessions.SignInScheme]);
    }

    public static async Task<NoContent> SignOut(HttpContext http)
    {
        await http.SignOutAsync(StaffIdentity.SessionScheme);
        return TypedResults.NoContent();
    }

    // Development only (mapped there, and startup refuses the setting elsewhere): a stand-in for the tenant's sign-in.
    // The CSRF header is required here too, so another site cannot sign a browser in (login CSRF).
    public static async Task<Results<NoContent, ProblemHttpResult>> DevelopmentSignIn(
        DevelopmentSignInRequest request, HttpContext http, StaffDirectory directory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!StaffSessions.HasCsrfHeader(http.Request))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "csrf-header-required", title: $"The {StaffIdentity.CsrfHeader} header is required.");
        }

        if (!RoleChangeRequest.IsCanonicalObjectId(request.ObjectId)
            || await directory.ResolveAsync(StaffSessions.DevelopmentIssuer, request.ObjectId, cancellationToken) is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "invalid-request", title: "objectId is a lowercase GUID.");
        }

        await http.SignInAsync(StaffIdentity.SessionScheme,
            StaffSessions.SessionPrincipal(StaffSessions.DevelopmentIssuer, request.ObjectId!), StaffSessions.Start(timeProvider));
        return TypedResults.NoContent();
    }

    // A path on this site only: never another origin ("//host", "/\host") and no control characters.
    internal static bool IsLocal(string? url) =>
        url is { Length: > 0 and <= MaxReturnUrlLength } && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && url.All(c => c is > ' ' and <= '~' && c != '\\');
}

/// <param name="StaffId">"staff:{id}", as in the audit log and timelines.</param>
/// <param name="Permissions">What the UI may offer; every call is still checked on the server.</param>
internal sealed record StaffSessionResponse(string StaffId, IReadOnlyList<string> Permissions);
