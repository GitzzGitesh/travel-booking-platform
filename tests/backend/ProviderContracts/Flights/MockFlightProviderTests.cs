using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.ProviderContracts.Flights;

/// <summary>The mock passes the shared contract, plus mock-only guarantees: determinism, scenarios, and guards.</summary>
public sealed class MockFlightProviderTests : FlightProviderSearchContract
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    protected override IFlightProvider Provider { get; } = Create(MockFlightScenario.Success.ToString());

    protected override DateTimeOffset Now => _now;

    [Fact]
    public async Task Same_criteria_and_clock_give_identical_offers()
    {
        var first = await SearchOffers(RoundTrip());
        var second = await Create(MockFlightScenario.Success.ToString()).SearchAsync(RoundTrip(), TestContext.Current.CancellationToken);

        second.Value.Offers.Select(Describe).ShouldBe(first.Select(Describe));
    }

    [Fact]
    public async Task Offers_expire_thirty_minutes_after_search_and_use_the_test_currency()
    {
        var offers = await SearchOffers(OneWay());

        offers.ShouldAllBe(o => o.ExpiresAt == _now.AddMinutes(30));
        offers.ShouldAllBe(o => o.TotalPrice.Currency.Value == "XTS");
    }

    [Fact]
    public async Task Infants_are_cheaper_than_seated_passengers()
    {
        var adultOnly = await SearchOffers(OneWay(new PassengerMix(1)));
        var withInfant = await SearchOffers(OneWay(new PassengerMix(1, 0, 1)));
        var twoAdults = await SearchOffers(OneWay(new PassengerMix(2)));

        withInfant[0].TotalPrice.Amount.ShouldBeGreaterThan(adultOnly[0].TotalPrice.Amount);
        withInfant[0].TotalPrice.Amount.ShouldBeLessThan(twoAdults[0].TotalPrice.Amount);
    }

    [Fact]
    public async Task No_results_scenario_returns_an_empty_success()
    {
        var result = await Create(nameof(MockFlightScenario.NoResults)).SearchAsync(OneWay(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Offers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(MockFlightScenario.Unavailable, ProviderErrorKind.Unavailable)]
    [InlineData(MockFlightScenario.RateLimited, ProviderErrorKind.RateLimited)]
    public async Task Failure_scenarios_return_taxonomy_errors(MockFlightScenario scenario, ProviderErrorKind expected)
    {
        var result = await Create(scenario.ToString()).SearchAsync(OneWay(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Other_destinations_revalidate_at_the_searched_price_with_a_fresh_expiry()
    {
        var clock = new FakeTimeProvider(_now);
        var provider = Create(MockFlightScenario.Success.ToString(), clock: clock);
        var offer = (await provider.SearchAsync(OneWay(), TestContext.Current.CancellationToken)).Value.Offers[1];
        clock.Advance(TimeSpan.FromMinutes(10));

        var revalidated = (await provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken)).Value;

        revalidated.TotalPrice.ShouldBe(offer.TotalPrice);
        revalidated.Reference.ShouldBe(offer.Reference);
        revalidated.ExpiresAt.ShouldBe(_now.AddMinutes(40));
    }

    [Fact]
    public async Task F01_the_price_changed_destination_revalidates_at_a_higher_price()
    {
        var offer = await SearchTo(MockRevalidationScenarios.PriceChangedDestination);

        var revalidated = (await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken)).Value;

        revalidated.TotalPrice.Currency.ShouldBe(offer.TotalPrice.Currency);
        revalidated.TotalPrice.Amount.ShouldBe(decimal.Round(offer.TotalPrice.Amount * MockRevalidationScenarios.PriceChangeFactor, 2));
        Itinerary(revalidated).ShouldBe(Itinerary(offer));
    }

    [Theory]
    [InlineData(MockRevalidationScenarios.OfferExpiredDestination, ProviderErrorKind.OfferExpired)]
    [InlineData(MockRevalidationScenarios.SoldOutDestination, ProviderErrorKind.SoldOut)]
    public async Task F02_and_F03_destinations_are_no_longer_bookable(string destination, ProviderErrorKind expected)
    {
        var offer = await SearchTo(destination);

        var result = await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("other", "mock-0-LHRJFK-20270214--Economy-1A0C0I")]
    [InlineData("mock", "mock-9-LHRJFK-20270214--Economy-1A0C0I")]
    [InlineData("mock", "mock-0-LHRLHR-20270214--Economy-1A0C0I")]
    [InlineData("mock", "mock-0-LHRJFK-20270214--Steerage-1A0C0I")]
    [InlineData("mock", "mock-0-LHRJFK-20270214--Economy-0A0C0I")]
    [InlineData("mock", "mock-0-LHRJFK-20271345--Economy-1A0C0I")] // an impossible date
    public async Task Foreign_or_malformed_references_are_invalid_requests(string providerId, string token)
    {
        var result = await Provider.RevalidateAsync(new ProviderOfferRef(providerId, token), TestContext.Current.CancellationToken);

        result.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task The_unavailable_scenario_also_fails_revalidation()
    {
        var offer = (await SearchOffers(OneWay()))[0];

        var result = await Create(nameof(MockFlightScenario.Unavailable)).RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken);

        result.Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    // An allow-list: production-like names that are not exactly "Production" are refused too.
    [Theory]
    [InlineData("Production")]
    [InlineData("Prod")]
    [InlineData("Production-EU")]
    public void The_mock_runs_only_in_development_or_staging(string environment) =>
        Should.Throw<OptionsValidationException>(() => Create(MockFlightScenario.Success.ToString(), environment))
            .Message.ShouldContain("only runs in Development or Staging");

    [Fact]
    public void The_mock_runs_in_staging() =>
        Should.NotThrow(() => Create(MockFlightScenario.Success.ToString(), Environments.Staging));

    [Fact]
    public void Undefined_scenarios_are_rejected() =>
        Should.Throw<OptionsValidationException>(() => Create("42"))
            .Message.ShouldContain("not a defined scenario");

    // Built through the public registration with configuration, as a host would. Resolving the provider reads the
    // options, which runs the same validation that ValidateOnStart runs at host startup.
    private async Task<FlightOffer> SearchTo(string destination)
    {
        var criteria = new FlightSearchCriteria(new AirportCode("LHR"), new AirportCode(destination), DateOnly.FromDateTime(_now.UtcDateTime).AddDays(30), null, new PassengerMix(2, 0, 1), CabinClass.Business);
        return (await SearchOffers(criteria))[2];
    }

    private static IFlightProvider Create(string scenario, string environment = "Development", TimeProvider? clock = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new($"{MockFlightProviderOptions.SectionName}:Scenario", scenario)])
            .Build();
        var services = new ServiceCollection()
            .AddSingleton(clock ?? new FakeTimeProvider(_now))
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
            .AddMockFlightProvider(configuration)
            .BuildServiceProvider();
        _ = services.GetRequiredService<IOptions<MockFlightProviderOptions>>().Value;
        return services.GetRequiredService<IFlightProvider>();
    }

    private static string Describe(FlightOffer offer) =>
        $"{offer.Reference} {offer.TotalPrice} {offer.ExpiresAt:O} " +
        string.Join(" | ", offer.Slices.SelectMany(s => s.Segments).Select(s => $"{s.FlightNumber} {s.Origin}-{s.Destination} {s.DepartureLocal:O}"));
}
