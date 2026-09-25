namespace TravelBooking.Integrations.Flights.Mock;

/// <summary>Search scenarios (docs/architecture/provider-integration.md). Selected by configuration, never by production data.</summary>
public enum MockFlightScenario
{
    Success,
    NoResults,
    Unavailable,
    RateLimited,
}

public sealed class MockFlightProviderOptions
{
    public const string SectionName = "Integrations:Flights:Mock";

    public MockFlightScenario Scenario { get; set; } = MockFlightScenario.Success;
}
