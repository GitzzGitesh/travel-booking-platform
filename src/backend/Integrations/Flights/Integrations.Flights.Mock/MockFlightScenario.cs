namespace TravelBooking.Integrations.Flights.Mock;

/// <summary>Search scenarios (docs/architecture/provider-integration.md). Selected by configuration, never by production data.</summary>
public enum MockFlightScenario
{
    Success,
    NoResults,
    Unavailable,
    RateLimited,
}

/// <summary>
/// Revalidation scenarios, chosen per offer by a reserved destination airport code (provider-integration.md: magic
/// values), so one running Api can demonstrate each: search to one of these codes, select an offer, then revalidate.
/// Any other destination revalidates at the searched price. Test-only codes: the mock does not model real airports.
/// </summary>
public static class MockRevalidationScenarios
{
    /// <summary>F-01: revalidation returns the offer at the searched price times <see cref="PriceChangeFactor"/>.</summary>
    public const string PriceChangedDestination = "ZPC";

    /// <summary>F-02: revalidation reports the offer expired.</summary>
    public const string OfferExpiredDestination = "ZEX";

    /// <summary>F-03: revalidation reports the offer sold out.</summary>
    public const string SoldOutDestination = "ZSO";

    public const decimal PriceChangeFactor = 1.15m;
}

public sealed class MockFlightProviderOptions
{
    public const string SectionName = "Integrations:Flights:Mock";

    public MockFlightScenario Scenario { get; set; } = MockFlightScenario.Success;
}
