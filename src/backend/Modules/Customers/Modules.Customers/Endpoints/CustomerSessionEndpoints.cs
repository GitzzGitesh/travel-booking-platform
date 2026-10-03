using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers.Application;

namespace TravelBooking.Modules.Customers.Endpoints;

/// <summary>Development only: sign in as this customer account (a lowercase GUID object id), without a tenant.</summary>
public sealed class DevelopmentCustomerSignInRequest
{
    [Required]
    [StringLength(36, MinimumLength = 36)]
    [RegularExpression("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    public string? ObjectId { get; init; }
}

/// <summary>
/// The customer-web session (ADR 0028). Thin: sign-in is the OpenID Connect handler's, and the signed-in customer comes
/// from the validated session or token only.
/// </summary>
internal static class CustomerSessionEndpoints
{
    public const int MaxReturnUrlLength = 2048;

    // For the UI only (what to show); the server checks every call itself.
    public static Ok<CustomerSessionResponse> Current(ClaimsPrincipal user) => TypedResults.Ok(new CustomerSessionResponse(user.CustomerId()!));

    public static async Task<Results<ChallengeHttpResult, ProblemHttpResult>> SignIn(IAuthenticationSchemeProvider schemes, string? returnUrl = null)
    {
        if (await schemes.GetSchemeAsync(CustomerSessions.SignInScheme) is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: "customer-sign-in-unavailable",
                title: "Sign-in is not available yet. Try again later.");
        }

        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = IsLocal(returnUrl) ? returnUrl : "/" }, [CustomerSessions.SignInScheme]);
    }

    public static async Task<NoContent> SignOut(HttpContext http)
    {
        await http.SignOutAsync(CustomerIdentity.SessionScheme);
        return TypedResults.NoContent();
    }

    // Development only (mapped there, and startup refuses the setting elsewhere): a stand-in for the tenant's sign-in.
    // The CSRF header is required here too, so another site cannot sign a browser in (login CSRF).
    public static async Task<Results<NoContent, ProblemHttpResult>> DevelopmentSignIn(
        DevelopmentCustomerSignInRequest request, HttpContext http, CustomerDirectory directory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!CustomerSessions.HasCsrfHeader(http.Request))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "csrf-header-required",
                title: $"The {CustomerIdentity.CsrfHeader} header is required.");
        }

        if (await directory.ResolveAsync(CustomerSessions.DevelopmentIssuer, request.ObjectId, cancellationToken) is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "invalid-request", title: "objectId is a lowercase GUID.");
        }

        await http.SignInAsync(CustomerIdentity.SessionScheme,
            CustomerSessions.SessionPrincipal(CustomerSessions.DevelopmentIssuer, request.ObjectId!), CustomerSessions.Start(timeProvider));
        return TypedResults.NoContent();
    }

    // A path on this site only: never another origin ("//host", "/\host") and no control characters (as for staff, ADR 0023).
    internal static bool IsLocal(string? url) =>
        url is { Length: > 0 and <= MaxReturnUrlLength } && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && url.All(c => c is > ' ' and <= '~' && c != '\\');
}

/// <param name="CustomerId">Our internal customer id; every call is still checked on the server.</param>
internal sealed record CustomerSessionResponse(string CustomerId);
