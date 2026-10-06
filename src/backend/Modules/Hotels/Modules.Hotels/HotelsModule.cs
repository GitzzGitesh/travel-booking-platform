using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Hotels.Application;
using TravelBooking.Modules.Hotels.Endpoints;
using TravelBooking.Modules.Hotels.Infrastructure;

namespace TravelBooking.Modules.Hotels;

/// <summary>
/// The Hotels module's entry point (ADR 0030). The host registers the module, maps its endpoints and composes an
/// <see cref="Ports.IHotelProvider"/> adapter (only the mock until a hotel supplier is chosen, Q6).
/// </summary>
public static class HotelsModule
{
    public static IServiceCollection AddHotelsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<HotelProviders>();
        services.AddScoped<SearchHotelsHandler>();
        services.AddScoped<SelectHotelOfferHandler>();
        services.AddScoped<RevalidateHotelSelectionHandler>();
        services.AddScoped<AcceptHotelPriceHandler>();
        services.AddHybridCache(options => options.MaximumPayloadBytes = HotelSearchCache.MaximumPayloadBytes);
        services.AddSingleton<HotelSearchCache>();

        // The module's own schema. The connection string is resolved on first use, so search works without a database.
        // Migrations are never applied at startup (database rules).
        services.AddDbContext<HotelsDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(HotelsDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{HotelsDbContext.ConnectionStringName}' is not configured."),
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", HotelsDbContext.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IHotelSelectionStore, SqlHotelSelectionStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapHotelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/hotels").WithTags("Hotels");

        // Public search: anonymous by design; it calls the supplier, so it has the tighter per-client limit.
        group.MapPost("/searches", HotelEndpoints.Search)
            .WithName("SearchHotels")
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        // Anonymous or a signed-in customer, who then owns the selection (only an owned one can be booked, Q8).
        group.MapPost("/selected-offers", HotelEndpoints.Select)
            .WithName("SelectHotelOffer")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        // Revalidation before any booking step (F-01..F-03): an owned selection is visible to its owner only.
        group.MapPost("/selected-offers/{selectedOfferId:guid}/revalidations", HotelEndpoints.Revalidate)
            .WithName("RevalidateSelectedHotelOffer")
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<HotelSelectionProblemResponse>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();

        group.MapPost("/selected-offers/{selectedOfferId:guid}/price-acceptances", HotelEndpoints.AcceptPrice)
            .WithName("AcceptSelectedHotelOfferPrice")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<HotelSelectionProblemResponse>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();
        return endpoints;
    }
}
