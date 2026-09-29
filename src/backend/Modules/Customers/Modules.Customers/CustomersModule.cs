using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Customers.Endpoints;
using TravelBooking.Modules.Customers.Infrastructure;

namespace TravelBooking.Modules.Customers;

/// <summary>
/// The Customers module (architecture overview: customer profiles, saved travellers, consents). Today it owns customer
/// authentication (ADR 0008): it validates customer bearer tokens (Entra External ID) and maps the account (the token's
/// issuer and user object id, <c>oid</c>) to our internal customer id, added in its own identity
/// (<see cref="CustomerIdentity"/>). No profile or traveller data is stored until retention (Q9) is decided.
/// </summary>
public static class CustomersModule
{
    /// <summary>
    /// <c>Authentication:Customers</c>. Tenant values come from configuration (Key Vault / user-secrets), never from
    /// code: <c>Authority</c> is the External ID tenant's OpenID Connect authority (its metadata supplies the issuer and
    /// signing keys), <c>Audience</c> the Api's application id, and <c>RequiredScope</c> the delegated scope customer
    /// tokens must carry (required once an authority is set). Until they are set, every customer token is refused.
    /// </summary>
    public const string SettingsSection = "Authentication:Customers";

    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CustomerDirectory>();
        services.AddScoped<ICustomerStore, SqlCustomerStore>();
        services.AddDbContext<CustomersDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(CustomersDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{CustomersDbContext.ConnectionStringName}' is not configured."),
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", CustomersDbContext.Schema)));

        // Personal data (Q9, ADR 0020): travellers, contacts and encrypted documents, with their retention.
        services.AddScoped<IPersonalDataStore, SqlPersonalDataStore>();
        services.AddScoped<SaveOrderTravellersHandler>();
        services.AddScoped<OrderTravellersQuery>();
        services.AddScoped<SaveTravelDocumentHandler>();
        services.AddScoped<IOrderTravellers, OrderTravellersReadinessQuery>();
        services.AddScoped<TravelDocumentReader>();
        services.AddScoped<LegalHoldHandler>();
        services.AddScoped<PersonalDataPurger>();
        services.AddSingleton<IDocumentProtector, AesGcmDocumentProtector>();
        services.AddOptions<DocumentEncryptionOptions>().Bind(configuration.GetSection(DocumentEncryptionOptions.SectionName))
            .Validate(AesGcmDocumentProtector.IsWellFormed, "Customers:DocumentEncryption keys must be base64 of 32 bytes, and ActiveKeyId must name one of them.")
            .ValidateOnStart();
        services.AddOptions<PersonalDataRetentionOptions>()
            .Bind(configuration.GetSection(PersonalDataRetentionOptions.SectionName))
            .Validate(o => o.PersonalDataMonthsAfterTravel > 0 && o.DocumentDaysAfterTravel > 0, "Customers:Retention periods must be positive.")
            .ValidateOnStart();
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)

        var settings = configuration.GetSection(SettingsSection);
        var authority = settings["Authority"] is { Length: > 0 } configuredAuthority ? configuredAuthority : null;
        var requiredScope = settings["RequiredScope"] is { Length: > 0 } scope ? scope : null;
        if (authority is not null && requiredScope is null)
        {
            // Without a scope, any token issued for the Api's audience (an app-only token, for instance) would count as a customer.
            throw new InvalidOperationException($"{SettingsSection}:RequiredScope is required when {SettingsSection}:Authority is set.");
        }

        // Only the customer endpoints authenticate customers (their policy names the scheme). The host suppresses the
        // automatic default scheme, so no token is validated (nor a customer looked up) on anonymous routes such as
        // search or the payment webhook.
        services.AddAuthentication()
            .AddJwtBearer(CustomerIdentity.Scheme, options =>
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
                    AuthenticationType = CustomerIdentity.Scheme,
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = MapToCustomerAsync };
            });

        services.AddAuthorization(options => options.AddPolicy(CustomerIdentity.Policy, policy =>
        {
            policy.AddAuthenticationSchemes(CustomerIdentity.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.CustomerId() is not null);
            if (requiredScope is not null)
            {
                policy.RequireAssertion(context => context.User.FindFirstValue("scp")?.Split(' ').Contains(requiredScope, StringComparer.Ordinal) == true);
            }
        }));
        return services;
    }

    /// <summary>The Worker's Customers job: applying the personal-data retention (Q9). Never registered in the Api.</summary>
    public static IServiceCollection AddCustomersBackgroundJobs(this IServiceCollection services)
    {
        services.AddBackgroundJob<PurgePersonalDataJob, CustomersDbContext>(PurgePersonalDataJob.Name, TimeSpan.FromHours(1));
        return services;
    }

    /// <summary>The signed-in customer's own endpoints.</summary>
    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // An order's travellers and booker contact (Q9): the owner's order only, while it awaits payment.
        var travellers = endpoints.MapGroup("/orders/{orderId:guid}/travellers").WithTags("Travellers")
            .RequireAuthorization(CustomerIdentity.Policy)
            .RequireCustomerWriteLimit(); // per customer (Q10); per address too, from the /api/v1 group
        travellers.MapPut("/", OrderTravellersEndpoints.Save)
            .WithName("SaveOrderTravellers")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        travellers.MapGet("/", OrderTravellersEndpoints.Get)
            .WithName("GetOrderTravellers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        travellers.MapPut("/{position:int}/document", OrderTravellersEndpoints.SaveDocument)
            .WithName("SaveTravelDocument")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGroup("/customers").WithTags("Customers")
            .MapGet("/me", CurrentCustomerEndpoint.Handle)
            .WithName("GetCurrentCustomer")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(CustomerIdentity.Policy);
        return endpoints;
    }

    // After the token's signature, issuer, audience and lifetime are validated: map the account to our customer. The
    // issuer and the user object id are read from the validated token itself, never by a (case-insensitive) claim lookup.
    private static async Task MapToCustomerAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;

        // Only we issue the internal customer id: a token that carries the claim in any spelling is refused.
        if (principal.Claims.Any(c => string.Equals(c.Type, CustomerIdentity.CustomerIdClaim, StringComparison.OrdinalIgnoreCase))
            || principal.Identities.Any(i => string.Equals(i.AuthenticationType, CustomerIdentity.MappedIdentityType, StringComparison.OrdinalIgnoreCase)))
        {
            context.Fail("The token carries a reserved claim.");
            return;
        }

        // A customer is a user: app-only tokens (Entra's idtyp "app") are refused.
        if (context.SecurityToken is not JsonWebToken token
            || (token.TryGetPayloadValue<string>("idtyp", out var tokenType) && string.Equals(tokenType, "app", StringComparison.OrdinalIgnoreCase))
            || !token.TryGetPayloadValue<string>("oid", out var objectId))
        {
            context.Fail("The token does not identify a customer account.");
            return;
        }

        var directory = context.HttpContext.RequestServices.GetRequiredService<CustomerDirectory>();
        if (await directory.ResolveAsync(token.Issuer, objectId, context.HttpContext.RequestAborted) is not { } customerId)
        {
            context.Fail("The token does not identify a customer account.");
            return;
        }

        principal.AddIdentity(new ClaimsIdentity([new Claim(CustomerIdentity.CustomerIdClaim, customerId)], CustomerIdentity.MappedIdentityType));
    }
}
