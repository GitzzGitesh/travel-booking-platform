using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>Flight provider composition settings (<c>Flights:SearchProviderId</c>).</summary>
internal sealed class FlightProviderComposition
{
    public string? SearchProviderId { get; set; }
}

/// <summary>
/// Checked at startup, in both hosts, so a bad composition never reaches a request:
/// - provider ids are unique;
/// - when any provider is composed, exactly one can be searched (the configured one, which implements search);
/// - outside Development and Staging, every composed adapter is <see cref="AdapterStage.ProductionReady"/>. The mock and
///   the documentation-mapped or scaffolded supplier adapters (Q6) therefore never run in Production.
/// </summary>
internal sealed class FlightProviderCompositionValidator(IEnumerable<IFlightProvider> providers, IHostEnvironment environment)
    : IValidateOptions<FlightProviderComposition>
{
    public ValidateOptionsResult Validate(string? name, FlightProviderComposition options)
    {
        var composed = providers.ToList();
        if (composed.Count == 0)
        {
            return ValidateOptionsResult.Success; // no flight search here (e.g. Production before a supplier is approved)
        }

        FlightProviders registry;
        try
        {
            registry = new FlightProviders(composed, options.SearchProviderId);
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }

        var failures = new List<string>();
        if (registry.SearchProviderProblem() is { } problem)
        {
            failures.Add(problem);
        }

        if (!environment.IsDevelopment() && !environment.IsStaging())
        {
            failures.AddRange(composed
                .Where(p => p.Capabilities.Stage != AdapterStage.ProductionReady)
                .Select(p => $"Flight provider '{p.Id}' is at stage {p.Capabilities.Stage}, and only ProductionReady adapters run in {environment.EnvironmentName}."));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
