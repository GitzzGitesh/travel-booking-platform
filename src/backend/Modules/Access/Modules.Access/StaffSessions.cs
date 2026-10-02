using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Modules.Access;

/// <summary>
/// <c>Authentication:StaffSession</c> (ADR 0023): the admin-web sign-in client (<c>Authority</c>, <c>ClientId</c> and
/// <c>ClientSecret</c> from user-secrets or Key Vault only) and the session lifetimes.
/// </summary>
internal sealed class StaffSessionOptions
{
    public const string SectionName = "Authentication:StaffSession";

    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Signed out after this long without a request (sliding).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Signed out this long after signing in, however active (absolute).</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>The Development-only sign-in stand-in (local work and E2E); startup refuses it anywhere else.</summary>
    public bool DevelopmentSignIn { get; set; }

    public bool SignInConfigured => !string.IsNullOrEmpty(Authority);

    public bool IsValid() =>
        IdleTimeout > TimeSpan.Zero && MaxLifetime >= IdleTimeout
        && (!SignInConfigured || (!string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)));
}

/// <summary>
/// The admin-web backend-for-frontend (ADR 0023): staff sign in with OpenID Connect on the server, into an HttpOnly,
/// SameSite=Strict session cookie that holds only the account (issuer and object id) and the sign-in time, never a
/// token or a permission. Every request maps the account again (staff id, permissions), exactly as for staff tokens.
/// </summary>
internal static class StaffSessions
{
    /// <summary>The OpenID Connect scheme that signs staff in (registered only once the tenant is configured).</summary>
    public const string SignInScheme = "StaffSignIn";

    public const string CookieName = "__Host-tb-staff";

    public const string CallbackPath = "/api/admin/v1/session/callback";

    /// <summary>The issuer of the Development sign-in stand-in: never a tenant's, so never a tenant staff member.</summary>
    public const string DevelopmentIssuer = "urn:travel-booking:development-sign-in";

    internal const string SignedInAtKey = "tb_signed_in_at";

    public static AuthenticationBuilder AddStaffSession(this AuthenticationBuilder builder, IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StaffSessionOptions>()
            .Bind(configuration.GetSection(StaffSessionOptions.SectionName))
            .Validate(o => o.IsValid(), $"{StaffSessionOptions.SectionName}: ClientId and ClientSecret are required with an Authority, and 0 < IdleTimeout <= MaxLifetime.")
            .Validate<IHostEnvironment>((o, environment) => !o.DevelopmentSignIn || environment.IsDevelopment(),
                $"{StaffSessionOptions.SectionName}:DevelopmentSignIn is allowed in Development only.")
            .ValidateOnStart();

        var settings = configuration.GetSection(StaffSessionOptions.SectionName).Get<StaffSessionOptions>() ?? new StaffSessionOptions();
        // Staff are identified by issuer and object id: sign-in uses the staff tenant itself (never a multi-tenant
        // endpoint), the same authority as staff tokens, so one person has one staff id.
        var tokenAuthority = configuration[$"{AccessModule.SettingsSection}:Authority"];
        if (settings.SignInConfigured
            && (string.IsNullOrEmpty(tokenAuthority)
                || !string.Equals(settings.Authority!.TrimEnd('/'), tokenAuthority.TrimEnd('/'), StringComparison.Ordinal)
                || new[] { "/common", "/organizations", "/consumers" }.Any(m => settings.Authority.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                $"{StaffSessionOptions.SectionName}:Authority must be the staff tenant's own authority, equal to {AccessModule.SettingsSection}:Authority.");
        }

        builder.AddCookie(StaffIdentity.SessionScheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = settings.IdleTimeout;
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = ValidateSessionAsync,
                // An API: no redirects to a login page, just the status.
                OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden),
                OnRedirectToLogout = context => Status(context, StatusCodes.Status204NoContent),
                OnRedirectToReturnUrl = context => Status(context, StatusCodes.Status204NoContent),
            };
        });

        if (settings.SignInConfigured)
        {
            builder.AddOpenIdConnect(SignInScheme, options =>
            {
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.SignInScheme = StaffIdentity.SessionScheme;
                options.CallbackPath = CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.SaveTokens = false; // the Api is the resource: the session needs the identity only
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                // The authorization response comes back by a top-level GET (query mode), so Lax suffices; never None.
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = SignedInAsync,
                    // admin-web is served at the origin's root (ADR 0023, hosting follow-up).
                    OnRemoteFailure = context =>
                    {
                        AccessModule.Refused(context.HttpContext, "sign-in-failed");
                        context.Response.Redirect("/?sign-in=failed");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
        }

        return builder;
    }

    /// <summary>The session's own identity: the account and nothing else (the mapping is added per request).</summary>
    public static AuthenticationProperties Start(TimeProvider timeProvider, string? redirectUri = null)
    {
        var properties = new AuthenticationProperties { IsPersistent = false, RedirectUri = redirectUri };
        properties.Items[SignedInAtKey] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        return properties;
    }

    public static ClaimsPrincipal SessionPrincipal(string issuer, string objectId) =>
        new(new ClaimsIdentity([new Claim("iss", issuer), new Claim("oid", objectId)], StaffIdentity.SessionScheme));

    // After the authorization code is redeemed and the ID token validated: the same refusals as for staff tokens
    // (reserved claims, no MFA, not a staff account), then a session holding only the account.
    internal static async Task SignedInAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        string? reason = null;
        if (AccessModule.HasReservedClaims(principal))
        {
            reason = "reserved-claim";
        }
        else if (!AccessModule.ShowsMfa(principal, context.HttpContext.RequestServices))
        {
            reason = "no-mfa";
        }
        else if (principal.FindFirst("iss")?.Value is not { } issuer || principal.FindFirst("oid")?.Value is not { } objectId
            || await AccessModule.MapStaffAsync(context.HttpContext, issuer, objectId) is null)
        {
            reason = "not-a-staff-account";
        }
        else
        {
            var started = Start(context.HttpContext.RequestServices.GetRequiredService<TimeProvider>(), context.Properties?.RedirectUri);
            context.Principal = SessionPrincipal(issuer, objectId);
            context.Properties = started;
            return;
        }

        AccessModule.Refused(context.HttpContext, $"sign-in-{reason}"); // the reason code only, never the token or account
        context.Fail($"The staff sign-in was refused ({reason}).");
    }

    // Every request: a well-formed, unexpired session, the CSRF header on unsafe requests, and the account mapped again.
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptionsMonitor<StaffSessionOptions>>().CurrentValue;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var principal = context.Principal!;
        var identity = principal.Identities.Count() == 1 ? principal.Identities.Single() : null;

        if (identity is null || identity.AuthenticationType != StaffIdentity.SessionScheme || AccessModule.HasReservedClaims(principal)
            || (identity.FindFirst("iss")?.Value == DevelopmentIssuer && !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()))
        {
            await RejectAsync(context, "invalid-session");
            return;
        }

        if (!context.Properties.Items.TryGetValue(SignedInAtKey, out var signedIn)
            || !DateTimeOffset.TryParseExact(signedIn, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var signedInAt)
            || signedInAt > now || now - signedInAt > options.MaxLifetime)
        {
            await RejectAsync(context, "session-expired");
            return;
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method)
            && !HasCsrfHeader(http.Request))
        {
            await RejectAsync(context, "csrf-header-missing");
            return;
        }

        if (await AccessModule.MapStaffAsync(http, identity.FindFirst("iss")?.Value, identity.FindFirst("oid")?.Value) is not { } mapped)
        {
            await RejectAsync(context, "not-a-staff-account");
            return;
        }

        // For this request only: a sliding renewal re-issues the original session, never the mapping.
        context.ReplacePrincipal(new ClaimsPrincipal([identity, mapped]));
    }

    public static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.TryGetValue(StaffIdentity.CsrfHeader, out var values) && values.Count == 1 && values[0] == "1";

    // Rejected and signed out: nothing is renewed for a refused request.
    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        AccessModule.Refused(context.HttpContext, $"session-{reason}");
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(StaffIdentity.SessionScheme);
    }

    private static Task Status<TOptions>(RedirectContext<TOptions> context, int status)
        where TOptions : AuthenticationSchemeOptions
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
