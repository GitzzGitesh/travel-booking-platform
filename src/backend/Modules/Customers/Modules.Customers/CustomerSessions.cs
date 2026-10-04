using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers.Application;

namespace TravelBooking.Modules.Customers;

/// <summary>
/// <c>Authentication:CustomerSession</c> (ADR 0028): the customer-web sign-in client (<c>Authority</c>, <c>ClientId</c>
/// and <c>ClientSecret</c> from user-secrets or Key Vault only) and the session lifetimes.
/// </summary>
internal sealed class CustomerSessionOptions
{
    public const string SectionName = "Authentication:CustomerSession";

    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Signed out after this long without a request (sliding).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Signed out this long after signing in, however active (absolute).</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The Development-only sign-in stand-in (local work and E2E); startup refuses it anywhere else.</summary>
    public bool DevelopmentSignIn { get; set; }

    public bool SignInConfigured => !string.IsNullOrEmpty(Authority);

    public bool IsValid() =>
        IdleTimeout > TimeSpan.Zero && MaxLifetime >= IdleTimeout
        && (!SignInConfigured || (!string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)));
}

/// <summary>
/// The customer-web backend-for-frontend (ADR 0028): customers sign in with OpenID Connect on the server, into an
/// HttpOnly session cookie that holds only the account (issuer and object id) and the sign-in time, never a token or
/// our customer id. Every request maps the account again, exactly as for customer tokens.
/// </summary>
internal static partial class CustomerSessions
{
    /// <summary>The OpenID Connect scheme that signs customers in (registered only once the tenant is configured).</summary>
    public const string SignInScheme = "CustomerSignIn";

    public const string CallbackPath = "/api/v1/session/callback";

    /// <summary>
    /// A hint for customer-web, readable by script: "a session may exist, ask for it". It carries no credential and
    /// proves nothing (the server never reads it); it spares every anonymous page view a session request.
    /// </summary>
    public const string HintCookieName = "tb-customer-hint";

    /// <summary>The issuer of the Development sign-in stand-in: never a tenant's, so never a real customer.</summary>
    public const string DevelopmentIssuer = "urn:travel-booking:development-customer-sign-in";

    internal const string SignedInAtKey = "tb_signed_in_at";

    public static AuthenticationBuilder AddCustomerSession(this AuthenticationBuilder builder, IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CustomerSessionOptions>()
            .Bind(configuration.GetSection(CustomerSessionOptions.SectionName))
            .Validate(o => o.IsValid(), $"{CustomerSessionOptions.SectionName}: ClientId and ClientSecret are required with an Authority, and 0 < IdleTimeout <= MaxLifetime.")
            .Validate<IHostEnvironment>((o, environment) => !o.DevelopmentSignIn || environment.IsDevelopment(),
                $"{CustomerSessionOptions.SectionName}:DevelopmentSignIn is allowed in Development only.")
            .ValidateOnStart();

        var settings = configuration.GetSection(CustomerSessionOptions.SectionName).Get<CustomerSessionOptions>() ?? new CustomerSessionOptions();
        // Customers are identified by issuer and object id: sign-in uses the customer tenant itself (never a multi-tenant
        // endpoint), the same authority as customer tokens, so one person has one customer id.
        var tokenAuthority = configuration[$"{CustomersModule.SettingsSection}:Authority"];
        if (settings.SignInConfigured
            && (string.IsNullOrEmpty(tokenAuthority)
                || !string.Equals(settings.Authority!.TrimEnd('/'), tokenAuthority.TrimEnd('/'), StringComparison.Ordinal)
                || new[] { "/common", "/organizations", "/consumers" }.Any(m => settings.Authority.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                $"{CustomerSessionOptions.SectionName}:Authority must be the customer tenant's own authority, equal to {CustomersModule.SettingsSection}:Authority.");
        }

        builder.AddCookie(CustomerIdentity.SessionScheme, options =>
        {
            options.Cookie.Name = CustomerIdentity.SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax; // arriving from an email or a payment redirect; unsafe requests need the CSRF header
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = settings.IdleTimeout;
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = ValidateSessionAsync,
                OnSignedIn = context =>
                {
                    context.Response.Cookies.Append(HintCookieName, "1", HintCookie());
                    return Task.CompletedTask;
                },
                // Signing out, or a refused session (which signs out): the hint goes too.
                OnSigningOut = context =>
                {
                    context.Response.Cookies.Delete(HintCookieName, HintCookie());
                    return Task.CompletedTask;
                },
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
                options.SignInScheme = CustomerIdentity.SessionScheme;
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
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = SignedInAsync,
                    // customer-web is served at the origin's root (ADR 0028).
                    OnRemoteFailure = context =>
                    {
                        Refused(context.HttpContext, "sign-in-failed");
                        context.Response.Redirect("/?sign-in=failed");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
        }

        return builder;
    }

    /// <summary>The session's own properties: the sign-in time (the mapping is added per request).</summary>
    public static AuthenticationProperties Start(TimeProvider timeProvider, string? redirectUri = null)
    {
        var properties = new AuthenticationProperties { IsPersistent = false, RedirectUri = redirectUri };
        properties.Items[SignedInAtKey] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        return properties;
    }

    public static ClaimsPrincipal SessionPrincipal(string issuer, string objectId) =>
        new(new ClaimsIdentity([new Claim("iss", issuer), new Claim("oid", objectId)], CustomerIdentity.SessionScheme));

    // After the authorization code is redeemed and the ID token validated: the same refusals as for customer tokens
    // (reserved claims, app-only identities, no usable account), then a session holding only the account.
    internal static async Task SignedInAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        string? reason = null;
        if (CustomersModule.HasReservedClaims(principal))
        {
            reason = "reserved-claim";
        }
        else if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase)
            || principal.FindFirst("iss")?.Value is not { } issuer || principal.FindFirst("oid")?.Value is not { } objectId
            || await context.HttpContext.RequestServices.GetRequiredService<CustomerDirectory>()
                .ResolveAsync(issuer, objectId, context.HttpContext.RequestAborted) is null)
        {
            reason = "not-a-customer-account";
        }
        else
        {
            context.Principal = SessionPrincipal(issuer, objectId);
            context.Properties = Start(context.HttpContext.RequestServices.GetRequiredService<TimeProvider>(), context.Properties?.RedirectUri);
            return;
        }

        Refused(context.HttpContext, $"sign-in-{reason}"); // the reason code only, never the token or account
        context.Fail($"The customer sign-in was refused ({reason}).");
    }

    // Every request: a well-formed, unexpired session, the CSRF header on unsafe requests, and the account mapped again.
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptionsMonitor<CustomerSessionOptions>>().CurrentValue;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var principal = context.Principal!;
        var identity = principal.Identities.Count() == 1 ? principal.Identities.Single() : null;

        if (identity is null || identity.AuthenticationType != CustomerIdentity.SessionScheme || CustomersModule.HasReservedClaims(principal)
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

        if (await http.RequestServices.GetRequiredService<CustomerDirectory>()
                .ResolveAsync(identity.FindFirst("iss")?.Value, identity.FindFirst("oid")?.Value, http.RequestAborted) is not { } customerId)
        {
            await RejectAsync(context, "not-a-customer-account");
            return;
        }

        // For this request only: a sliding renewal re-issues the original session, never the mapping.
        context.ReplacePrincipal(new ClaimsPrincipal(
            [identity, new ClaimsIdentity([new Claim(CustomerIdentity.CustomerIdClaim, customerId)], CustomerIdentity.MappedIdentityType)]));
    }

    private static CookieOptions HintCookie() =>
        new() { HttpOnly = false, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true };

    public static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.TryGetValue(CustomerIdentity.CsrfHeader, out var values) && values.Count == 1 && values[0] == "1";

    // Rejected and signed out: nothing is renewed for a refused request.
    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        Refused(context.HttpContext, $"session-{reason}");
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CustomerIdentity.SessionScheme);
    }

    // A security event, with a reason code, the route and the trace only: never the account, issuer or cookie.
    internal static void Refused(HttpContext http, string reason) =>
        LogRefused(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CustomerSessions).FullName!),
            reason, http.Request.Path.Value ?? string.Empty, http.TraceIdentifier);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "CustomerSessionRefused",
        Message = "Security: a customer session was refused ({Reason}) on {Path} (trace {TraceId})")]
    private static partial void LogRefused(ILogger logger, string reason, string path, string traceId);

    private static Task Status<TOptions>(RedirectContext<TOptions> context, int status)
        where TOptions : AuthenticationSchemeOptions
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
