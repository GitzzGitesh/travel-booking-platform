using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.ProviderContracts.Flights;

/// <summary>
/// The shared provider contract against a supplier's SANDBOX (Q6), credential-gated: without the environment
/// variables every test is reported as skipped, never as passed. Booking tests are skipped while the adapter does not
/// implement booking. Promote an adapter to <see cref="AdapterStage.SandboxVerified"/> only after these pass.
/// Never point these at production credentials.
/// </summary>
public abstract class SupplierSandboxContract : FlightProviderSearchContract
{
    private readonly Lazy<IFlightProvider?> _provider;

    protected SupplierSandboxContract() => _provider = new(Create);

    protected override IFlightProvider Provider => _provider.Value ?? Skip();

    protected override DateTimeOffset Now => DateTimeOffset.UtcNow; // a real sandbox runs on real time

    protected abstract string Supplier { get; }

    /// <summary>Environment variable → configuration key. All must be set, or the class is skipped.</summary>
    protected abstract IReadOnlyDictionary<string, string> Settings { get; }

    protected abstract IServiceCollection Register(IServiceCollection services, IConfiguration configuration);

    private IFlightProvider? Create()
    {
        var values = Settings.ToDictionary(s => s.Value, s => Environment.GetEnvironmentVariable(s.Key));
        if (values.Values.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        values[$"Integrations:Flights:{Supplier}:Enabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = Register(new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System), configuration).BuildServiceProvider();
        return services.GetRequiredService<IFlightProvider>();
    }

    private IFlightProvider Skip()
    {
        Assert.Skip($"{Supplier} sandbox credentials are not configured ({string.Join(", ", Settings.Keys)}).");
        return null!;
    }
}

public sealed class DuffelSandboxContractTests : SupplierSandboxContract
{
    protected override string Supplier => "Duffel";

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["DUFFEL_SANDBOX_BASE_URL"] = "Integrations:Flights:Duffel:BaseUrl",
        ["DUFFEL_SANDBOX_TOKEN"] = "Integrations:Flights:Duffel:AccessToken",
        ["DUFFEL_API_VERSION"] = "Integrations:Flights:Duffel:ApiVersion",
    };

    protected override IServiceCollection Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddDuffelFlightProvider(configuration);
}

public sealed class AmadeusSandboxContractTests : SupplierSandboxContract
{
    protected override string Supplier => "Amadeus";

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["AMADEUS_TEST_BASE_URL"] = "Integrations:Flights:Amadeus:BaseUrl",
        ["AMADEUS_TEST_CLIENT_ID"] = "Integrations:Flights:Amadeus:ClientId",
        ["AMADEUS_TEST_CLIENT_SECRET"] = "Integrations:Flights:Amadeus:ClientSecret",
    };

    protected override IServiceCollection Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddAmadeusFlightProvider(configuration);
}
