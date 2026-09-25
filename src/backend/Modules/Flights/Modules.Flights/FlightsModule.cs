using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Endpoints;
using TravelBooking.Modules.Flights.Infrastructure;

namespace TravelBooking.Modules.Flights;

/// <summary>
/// The Flights module's entry point. The host registers the module, maps its endpoints, and composes an
/// <see cref="Ports.IFlightProvider"/> adapter (ADR 0004, ADR 0014).
/// </summary>
public static class FlightsModule
{
    public static IServiceCollection AddFlightsModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The .NET 10 validation source generator only sees endpoints in the compilation that calls
        // AddValidation, so each module calls it for its own request types (ADR 0003).
        services.AddValidation();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SearchFlightsHandler>();
        services.AddScoped<SelectFlightOfferHandler>();
        services.AddScoped<RevalidateSelectedOfferHandler>();
        services.AddScoped<AcceptSelectedOfferPriceHandler>();

        // Supplier booking calls for the Phase 3 order orchestration; no endpoint maps them yet (ADR 0005).
        services.AddScoped<FlightSupplierBooking>();

        // Search results are held in HybridCache (ADR 0011: in-memory L1; Redis L2 later is configuration only).
        services.AddHybridCache(options => options.MaximumPayloadBytes = FlightSearchCache.MaximumPayloadBytes);
        services.AddSingleton<FlightSearchCache>();

        // The module's own schema. The connection string is resolved when the context is first used, so search works
        // in environments without a database. Migrations are never applied at startup (database rules).
        services.AddDbContext<FlightsDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(FlightsDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{FlightsDbContext.ConnectionStringName}' is not configured."),
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", FlightsDbContext.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<ISelectedOfferStore, SqlSelectedOfferStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapFlightsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/flights").WithTags("Flights");

        // Public search: anonymous by design (security rules). It calls the supplier, so it has the tighter per-client
        // limit; the host's /api/v1 group limits every other endpoint here.
        group.MapPost("/searches", SearchFlightsEndpoint.Handle)
            .WithName("SearchFlights")
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        // Anonymous until identity exists (Phase 4); like search, it must be rate limited before leaving Development.
        group.MapPost("/selected-offers", SelectFlightOfferEndpoint.Handle)
            .WithName("SelectFlightOffer")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        // Revalidation before any booking step (F-01..F-03). Anonymous like selection until identity exists (Phase 4):
        // the selection id is an unguessable server-issued id, and both must be rate limited before leaving Development.
        group.MapPost("/selected-offers/{selectedOfferId:guid}/revalidations", SelectedOfferRevalidationEndpoints.Revalidate)
            .WithName("RevalidateSelectedFlightOffer")
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<SelectedOfferProblemResponse>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        group.MapPost("/selected-offers/{selectedOfferId:guid}/price-acceptances", SelectedOfferRevalidationEndpoints.AcceptPrice)
            .WithName("AcceptSelectedFlightOfferPrice")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<SelectedOfferProblemResponse>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        return endpoints;
    }
}
