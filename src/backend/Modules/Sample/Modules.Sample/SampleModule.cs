using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.Modules.Sample.Endpoints;

namespace TravelBooking.Modules.Sample;

/// <summary>
/// The module's only public entry point. The Api host registers and maps the module through it.
/// </summary>
public static class SampleModule
{
    public static IServiceCollection AddSampleModule(this IServiceCollection services)
    {
        // The .NET 10 validation source generator only sees endpoints in the compilation that calls
        // AddValidation, so each module calls it for its own request types (ADR 0003).
        services.AddValidation();

        return services;
    }

    public static IEndpointRouteBuilder MapSampleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/sample").WithTags("Sample");

        group.MapPost("/validation", ValidateSampleEndpoint.Handle)
            .WithName("ValidateSample")
            .ProducesValidationProblem()
            .AllowAnonymous();

        return endpoints;
    }
}
