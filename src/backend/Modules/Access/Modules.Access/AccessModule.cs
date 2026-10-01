using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// the mapping of a staff account to our internal staff id, and the permission policies admin endpoints use. It has no
/// endpoints of its own yet.
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

        // Only admin endpoints authenticate staff (their policies name the scheme). Until a tenant is configured, every
        // staff token is refused (no authority, no audience): admin access fails closed.
        services.AddAuthentication()
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

        services.AddAuthorization(options =>
        {
            foreach (var permission in StaffPermissions.All)
            {
                options.AddPolicy(StaffIdentity.PolicyFor(permission), policy =>
                {
                    policy.AddAuthenticationSchemes(StaffIdentity.Scheme)
                        .RequireAuthenticatedUser()
                        .RequireAssertion(context => context.User.StaffId() is not null && context.User.HasPermission(permission));
                    if (requiredScope is not null)
                    {
                        policy.RequireAssertion(context => context.User.FindFirstValue("scp")?.Split(' ').Contains(requiredScope, StringComparer.Ordinal) == true);
                    }
                });
            }
        });
        return services;
    }

    // After the token's signature, issuer, audience and lifetime are validated: require MFA, map the account to our staff
    // member, and grant its roles' permissions. The issuer and object id are read from the validated token itself.
    private static async Task MapToStaffAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;

        // Only we issue the staff id and permissions: a token carrying them, in any spelling, is refused.
        if (principal.Claims.Any(c => string.Equals(c.Type, StaffIdentity.StaffIdClaim, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Type, StaffIdentity.PermissionClaim, StringComparison.OrdinalIgnoreCase))
            || principal.Identities.Any(i => string.Equals(i.AuthenticationType, StaffIdentity.MappedIdentityType, StringComparison.OrdinalIgnoreCase)))
        {
            Fail(context, "reserved-claim");
            return;
        }

        // Staff sign in with MFA (ADR 0008: MFA and Conditional Access mandatory). Conditional Access enforces it in the
        // tenant; the Api refuses a token that does not show it too (defence in depth, fails closed). The signal is the
        // tenant's choice: a Conditional Access authentication context in "acrs" (MfaAuthenticationContext, e.g. "c1"),
        // or else "amr" containing "mfa" (ADR 0022: to be confirmed against the tenant's access tokens).
        var mfaContext = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()[$"{SettingsSection}:MfaAuthenticationContext"];
        var mfaShown = mfaContext is { Length: > 0 }
            ? principal.FindAll("acrs").Any(c => string.Equals(c.Value, mfaContext, StringComparison.Ordinal))
            : principal.FindAll("amr").Any(c => string.Equals(c.Value, "mfa", StringComparison.Ordinal));
        if (!mfaShown)
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

        var services = context.HttpContext.RequestServices;
        if (await services.GetRequiredService<StaffDirectory>().ResolveAsync(token.Issuer, objectId, context.HttpContext.RequestAborted) is not { } staffId)
        {
            Fail(context, "not-a-staff-account");
            return;
        }

        // Read live on every sign-in: the configuration bootstrap plus approved managed grants, so an approved revocation
        // (or a grant removed from configuration) takes effect at once.
        var granted = await services.GetRequiredService<IRoleGrantStore>().FindActiveRolesAsync(objectId, context.HttpContext.RequestAborted);
        var permissions = services.GetRequiredService<IOptionsMonitor<AccessOptions>>().CurrentValue.PermissionsFor(objectId)
            .Union(StaffRoles.PermissionsOf(granted), StringComparer.Ordinal).ToList();
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(StaffIdentity.StaffIdClaim, staffId), .. permissions.Select(p => new Claim(StaffIdentity.PermissionClaim, p))],
            StaffIdentity.MappedIdentityType));
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
        return endpoints;
    }

    private static void Fail(TokenValidatedContext context, string reason)
    {
        Refused(context.HttpContext, reason);
        context.Fail("The staff token was refused.");
    }

    // A security event (security rules: authentication failures), with a reason code, the route and the trace only:
    // never the token, the object id or the issuer.
    private static void Refused(HttpContext http, string reason) =>
        LogRefused(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AccessModule).FullName!),
            reason, http.Request.Path.Value ?? string.Empty, http.TraceIdentifier);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "StaffTokenRefused", Message = "Security: a staff token was refused ({Reason}) on {Path} (trace {TraceId})")]
    private static partial void LogRefused(ILogger logger, string reason, string path, string traceId);
}
