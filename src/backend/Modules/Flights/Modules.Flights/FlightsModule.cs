using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Endpoints;

namespace TravelBooking.Modules.Flights;

/// <summary>
/// The Flights module's entry point. The host registers the module, maps its endpoints, and composes an
/// <see cref="Ports.IFlightProvider"/> adapter (ADR 0004, ADR 0014).
/// </summary>
public static class FlightsModule
{
    public static IServiceCollection AddFlightsModule(this IServiceCollection services)
    {
        // The .NET 10 validation source generator only sees endpoints in the compilation that calls
        // AddValidation, so each module calls it for its own request types (ADR 0003).
        services.AddValidation();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SearchFlightsHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapFlightsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/flights").WithTags("Flights");

        // Public search: anonymous by design (security rules). Must be rate limited before it is exposed outside
        // Development (docs/progress.md).
        group.MapPost("/searches", SearchFlightsEndpoint.Handle)
            .WithName("SearchFlights")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        return endpoints;
    }
}
