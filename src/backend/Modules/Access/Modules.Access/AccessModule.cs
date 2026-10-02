using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Application;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Access.Endpoints;
using TravelBooking.Modules.Access.Infrastructure;

namespace TravelBooking.Modules.Access;

/// <summary>
/// The Access module's entry point (ADR 0008, ADR 0022): staff authentication (the workforce tenant, MFA required),
/// the admin-web session (ADR 0023), the mapping of a staff account to our internal staff id, and the permission
/// policies admin endpoints use.
/// </summary>
public static partial class AccessModule
{
    /// <summary>Tenant values (<c>Authority</c>, <c>Audience</c>, <c>RequiredScope</c>) come from user-secrets or Key Vault.</summary>
    public const string SettingsSection = "Authentication:Staff";

    public static IServiceCollection AddAccessModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<AccessOptions>()
            .Bind(configuration.GetSection(AccessOptions.SectionName))
            .Validate(o => o.IsValid(), "Access:RoleAssignments needs one entry per object id, each with known roles.")
            .ValidateOnStart();

        services.AddDbContext<AccessDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(AccessDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{AccessDbContext.ConnectionStringName}' is not configured."),
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", AccessDbContext.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IStaffStore, SqlStaffStore>();
        services.AddScoped<StaffDirectory>();
        services.AddScoped<IRoleGrantStore, SqlRoleGrantStore>();
        services.AddScoped<RoleChangeHandler>();
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, StaffAuthorizationEvents>();

        var settings = configuration.GetSection(SettingsSection);
        var authority = settings["Authority"] is { Length: > 0 } configuredAuthority ? configuredAuthority : null;
        var requiredScope = settings["RequiredScope"] is { Length: > 0 } scope ? scope : null;
        if (authority is not null && requiredScope is null)
        {
            // Without a scope, any token issued for the Api's audience (an app-only token, for instance) would count as staff.
            throw new InvalidOperationException($"{SettingsSection}:RequiredScope is required when {SettingsSection}:Authority is set.");
        }

        // Only admin endpoints authenticate staff (their policies name the schemes). Until a tenant is configured, every
        // staff token is refused (no authority, no audience) and staff sign-in is unavailable: admin access fails closed.
        services.AddAuthentication()
            .AddStaffSession(services, configuration)
            .AddJwtBearer(StaffIdentity.Scheme, options =>
            {
                options.Authority = authority;
                options.Audience = settings["Audience"] is { Length: > 0 } audience ? audience : null;
                options.RequireHttpsMetadata = true;
                options.IncludeErrorDetails = false; // a refused token is not explained to the caller
                options.MapInboundClaims = false; // keep the token's claim names as issued
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromMinutes(2),
                    AuthenticationType = StaffIdentity.Scheme,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = MapToStaffAsync,
                    // A token that fails validation (signature, issuer, audience, lifetime) is a security event too.
                    OnAuthenticationFailed = context =>
                    {
                        Refused(context.HttpContext, "invalid-token");
                        return Task.CompletedTask;
                    },
                };
            });

        // A staff token or the admin-web session (ADR 0023), never both at once (StaffIdentity refuses an ambiguous staff
        // identity). The scope check applies to tokens: the session has no scope.
        void StaffPolicy(AuthorizationPolicyBuilder policy, Func<ClaimsPrincipal, bool> allowed)
        {
            policy.AddAuthenticationSchemes(StaffIdentity.Scheme, StaffIdentity.SessionScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.StaffId() is not null && allowed(context.User));
            if (requiredScope is not null)
            {
                policy.RequireAssertion(context =>
                    !context.User.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == StaffIdentity.Scheme)
                    || context.User.FindFirstValue("scp")?.Split(' ').Contains(requiredScope, StringComparer.Ordinal) == true);
            }
        }

        services.AddAuthorization(options =>
        {
            foreach (var permission in StaffPermissions.All)
            {
                options.AddPolicy(StaffIdentity.PolicyFor(permission), policy => StaffPolicy(policy, user => user.HasPermission(permission)));
            }

            options.AddPolicy(StaffIdentity.SignedInPolicy, policy => StaffPolicy(policy, _ => true));
        });
        return services;
    }

    // After the token's signature, issuer, audience and lifetime are validated: require MFA, map the account to our staff
    // member, and grant its roles' permissions. The issuer and object id are read from the validated token itself.
    private static async Task MapToStaffAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;

        if (HasReservedClaims(principal))
        {
            Fail(context, "reserved-claim");
            return;
        }

        if (!ShowsMfa(principal, context.HttpContext.RequestServices))
        {
            Fail(context, "no-mfa");
            return;
        }

        // A staff member is a user: app-only tokens (Entra's idtyp "app") are refused.
        if (context.SecurityToken is not JsonWebToken token
            || (token.TryGetPayloadValue<string>("idtyp", out var tokenType) && string.Equals(tokenType, "app", StringComparison.OrdinalIgnoreCase))
            || !token.TryGetPayloadValue<string>("oid", out var objectId))
        {
            Fail(context, "not-a-staff-account");
            return;
        }

        if (await MapStaffAsync(context.HttpContext, token.Issuer, objectId) is not { } mapped)
        {
            Fail(context, "not-a-staff-account");
            return;
        }

        principal.AddIdentity(mapped);
    }

    // Only we issue the staff id and permissions: a token or session carrying them, in any spelling, is refused.
    internal static bool HasReservedClaims(ClaimsPrincipal principal) =>
        principal.Claims.Any(c => string.Equals(c.Type, StaffIdentity.StaffIdClaim, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Type, StaffIdentity.PermissionClaim, StringComparison.OrdinalIgnoreCase))
        || principal.Identities.Any(i => string.Equals(i.AuthenticationType, StaffIdentity.MappedIdentityType, StringComparison.OrdinalIgnoreCase));

    // Staff sign in with MFA (ADR 0008: MFA and Conditional Access mandatory). Conditional Access enforces it in the
    // tenant; the Api refuses a token (or a sign-in's ID token) that does not show it too (defence in depth, fails
    // closed). The signal is the tenant's choice: a Conditional Access authentication context in "acrs"
    // (MfaAuthenticationContext, e.g. "c1"), or else "amr" containing "mfa" (ADR 0022: to be confirmed against the tenant).
    internal static bool ShowsMfa(ClaimsPrincipal principal, IServiceProvider services)
    {
        var mfaContext = services.GetRequiredService<IConfiguration>()[$"{SettingsSection}:MfaAuthenticationContext"];
        return mfaContext is { Length: > 0 }
            ? principal.FindAll("acrs").Any(c => string.Equals(c.Value, mfaContext, StringComparison.Ordinal))
            : principal.FindAll("amr").Any(c => string.Equals(c.Value, "mfa", StringComparison.Ordinal));
    }

    // The account's internal staff id (created on first sign-in) and its permissions, read live on every request: the
    // configuration bootstrap plus approved managed grants, so an approved revocation (or a grant removed from
    // configuration) takes effect at once. Null when the account is not a valid staff identity.
    internal static async Task<ClaimsIdentity?> MapStaffAsync(HttpContext http, string? issuer, string? objectId)
    {
        var services = http.RequestServices;
        if (await services.GetRequiredService<StaffDirectory>().ResolveAsync(issuer, objectId, http.RequestAborted) is not { } staffId)
        {
            return null;
        }

        var granted = await services.GetRequiredService<IRoleGrantStore>().FindActiveRolesAsync(objectId!, http.RequestAborted);
        var permissions = services.GetRequiredService<IOptionsMonitor<AccessOptions>>().CurrentValue.PermissionsFor(objectId!)
            .Union(StaffRoles.PermissionsOf(granted), StringComparer.Ordinal).ToList();
        return new ClaimsIdentity(
            [new Claim(StaffIdentity.StaffIdClaim, staffId), .. permissions.Select(p => new Claim(StaffIdentity.PermissionClaim, p))],
            StaffIdentity.MappedIdentityType);
    }

    /// <summary>
    /// The managed role grants (ADR 0022, maker-checker), under the admin route group: requests and decisions are audited
    /// in the Access schema in the same save.
    /// </summary>
    public static IEndpointRouteBuilder MapAccessAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/access").WithTags("Access (staff)");

        group.MapGet("/role-changes", AdminAccessEndpoints.ListRequests)
            .WithName("ListRoleChanges")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.AccessGrantsRead))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/role-grants", AdminAccessEndpoints.ListGrants)
            .WithName("ListRoleGrants")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.AccessGrantsRead))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/role-changes", AdminAccessEndpoints.Request)
            .WithName("RequestRoleChange")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.AccessGrantsRequest))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/role-changes/{requestId:guid}/decision", AdminAccessEndpoints.Decide)
            .WithName("DecideRoleChange")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.AccessGrantsApprove))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        MapSessionEndpoints(endpoints);
        return endpoints;
    }

    // The admin-web session (ADR 0023). Sign-in is anonymous by nature; everything else needs a signed-in staff member.
    private static void MapSessionEndpoints(IEndpointRouteBuilder endpoints)
    {
        var session = endpoints.MapGroup("/session").WithTags("Staff session");

        session.MapGet("/", StaffSessionEndpoints.Current)
            .WithName("GetStaffSession")
            .RequireAuthorization(StaffIdentity.SignedInPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        session.MapGet("/sign-in", StaffSessionEndpoints.SignIn)
            .WithName("SignInStaff")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        session.MapPost("/sign-out", (Delegate)StaffSessionEndpoints.SignOut)
            .WithName("SignOutStaff")
            .RequireAuthorization(StaffIdentity.SignedInPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var services = endpoints.ServiceProvider;
        if (services.GetRequiredService<IHostEnvironment>().IsDevelopment()
            && services.GetRequiredService<IOptions<StaffSessionOptions>>().Value.DevelopmentSignIn)
        {
            session.MapPost("/development-sign-in", StaffSessionEndpoints.DevelopmentSignIn)
                .WithName("SignInStaffForDevelopment")
                .AllowAnonymous()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }
    }

    private static void Fail(TokenValidatedContext context, string reason)
    {
        Refused(context.HttpContext, reason);
        context.Fail("The staff token was refused.");
    }

    // A security event (security rules: authentication failures), with a reason code, the route and the trace only:
    // never the token, the object id or the issuer.
    internal static void Refused(HttpContext http, string reason) =>
        LogRefused(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AccessModule).FullName!),
            reason, http.Request.Path.Value ?? string.Empty, http.TraceIdentifier);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "StaffTokenRefused", Message = "Security: a staff credential was refused ({Reason}) on {Path} (trace {TraceId})")]
    private static partial void LogRefused(ILogger logger, string reason, string path, string traceId);
}
