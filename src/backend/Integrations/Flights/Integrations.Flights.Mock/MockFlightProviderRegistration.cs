using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Mock;

public static class MockFlightProviderRegistration
{
    /// <summary>
    /// Registers the mock as the <see cref="IFlightProvider"/>. It refuses to run in Production and rejects undefined
    /// scenarios, at host startup and on first use (provider-integration.md: guarded by environment and configuration).
    /// </summary>
    public static IServiceCollection AddMockFlightProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MockFlightProviderOptions>()
            .Bind(configuration.GetSection(MockFlightProviderOptions.SectionName))
            .Validate<IHostEnvironment>((_, environment) => !environment.IsProduction(), "The mock flight provider must never run in Production.")
            .Validate(options => Enum.IsDefined(options.Scenario), $"{MockFlightProviderOptions.SectionName}:Scenario is not a defined scenario.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFlightProvider, MockFlightProvider>();
        return services;
    }
}
