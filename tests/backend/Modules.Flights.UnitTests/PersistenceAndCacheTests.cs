using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Infrastructure;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

public sealed class PersistenceAndCacheTests
{
    // A row as stored by version 1. It must stay readable for as long as such rows exist.
    private const string _storedVersion1 =
        """{"v":1,"slices":[{"segments":[{"carrier":"ZZ","flightNumber":"ZZ123","origin":"LHR","destination":"JFK","departureLocal":"2027-02-14T07:05:00","arrivalLocal":"2027-02-14T09:20:00"}]}]}""";

    [Fact]
    public void A_stored_version_1_snapshot_is_read_back_as_local_times()
    {
        var segment = SliceSnapshotJson.Read(_storedVersion1).ShouldHaveSingleItem().Segments.ShouldHaveSingleItem();

        segment.MarketingCarrier.ShouldBe("ZZ");
        segment.Origin.ShouldBe(new AirportCode("LHR"));
        segment.Destination.ShouldBe(new AirportCode("JFK"));
        segment.DepartureLocal.ShouldBe(new DateTime(2027, 2, 14, 7, 5, 0));
        segment.DepartureLocal.Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    // Version 1 with the optional leg facts (operating carrier, stated duration, fare basis): older readers ignore them.
    private const string _storedVersion1WithLegFacts =
        """{"v":1,"slices":[{"segments":[{"carrier":"ZZ","flightNumber":"ZZ123","origin":"LHR","destination":"JFK","departureLocal":"2027-02-14T07:05:00","arrivalLocal":"2027-02-14T09:20:00","operatingCarrier":"ZY","durationMinutes":495,"fareBasis":"M1MOCK"}]}]}""";

    [Fact]
    public void A_version_1_snapshot_has_no_operating_carrier_duration_or_fare_basis()
    {
        var segment = SliceSnapshotJson.Read(_storedVersion1).Single().Segments.Single();

        (segment.OperatingCarrier, segment.Duration, segment.FareBasis).ShouldBe((null, null, null));
    }

    [Fact]
    public void Written_snapshots_use_the_version_1_contract_and_omit_unstated_facts()
    {
        SliceSnapshotJson.Write(SliceSnapshotJson.Read(_storedVersion1)).ShouldBe(_storedVersion1);
        SliceSnapshotJson.Write(SliceSnapshotJson.Read(_storedVersion1WithLegFacts)).ShouldBe(_storedVersion1WithLegFacts);
    }

    [Fact]
    public void The_optional_leg_facts_round_trip()
    {
        var segment = SliceSnapshotJson.Read(_storedVersion1WithLegFacts).Single().Segments.Single();

        (segment.OperatingCarrier, segment.Duration, segment.FareBasis).ShouldBe(("ZY", TimeSpan.FromMinutes(495), "M1MOCK"));
    }

    [Fact]
    public void Unknown_snapshot_versions_fail_loudly()
    {
        Should.Throw<InvalidOperationException>(() => SliceSnapshotJson.Read("""{"v":99,"slices":[]}""")).Message.ShouldContain("version 99");
        Should.Throw<InvalidOperationException>(() => FareSnapshotJson.Read("""{"v":99,"refund":"Free","change":"Free"}""")).Message.ShouldContain("version 99");
    }

    [Fact]
    public void A_fare_snapshot_keeps_every_fact_exactly()
    {
        var fare = new FlightFare
        {
            PriceBreakdown = new FlightPriceBreakdown(
            [
                new PassengerFare(PassengerType.Adult, 2, Xts(100.10m), Xts(17.66m)),
                new PassengerFare(PassengerType.Infant, 1, Xts(10.01m), Xts(1.77m)),
            ]),
            ValidatingCarrier = "ZZ",
            Baggage = new BaggageAllowance(1, 1, 23),
            Conditions = new FareConditions(FareAllowance.AllowedWithFee, FareAllowance.Free),
            TicketingDeadline = new DateTimeOffset(2027, 1, 16, 9, 0, 0, TimeSpan.Zero),
        };

        var read = FareSnapshotJson.Read(FareSnapshotJson.Write(fare));

        FareSnapshotJson.Write(read).ShouldBe(FareSnapshotJson.Write(fare));
        read.PriceBreakdown!.Total.ShouldBe(Xts(247.30m));
        (read.ValidatingCarrier, read.Baggage, read.Conditions, read.TicketingDeadline).ShouldBe((fare.ValidatingCarrier, fare.Baggage, fare.Conditions, fare.TicketingDeadline));
    }

    [Fact]
    public void A_fare_the_supplier_said_nothing_about_stays_not_stated() =>
        FareSnapshotJson.Read(FareSnapshotJson.Write(FlightFare.NotStated)).ShouldBe(FlightFare.NotStated);

    private static Money Xts(decimal amount) => new(amount, new CurrencyCode("XTS"));

    [Fact]
    public async Task A_search_too_large_to_cache_is_detected_and_logged()
    {
        var logger = new RecordingLogger();
        var searchCache = new FlightSearchCache(SearchFlightsHandlerTests.NewCache(), logger, maximumPayloadBytes: 64);

        var stored = await searchCache.StoreAsync(Search(), TimeSpan.FromMinutes(30), TestContext.Current.CancellationToken);

        stored.ShouldBeFalse();
        logger.Errors.ShouldHaveSingleItem().ShouldContain("was not cached");
        (await searchCache.FindAsync(Search().SearchId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_normal_search_is_cached_and_found()
    {
        var searchCache = new FlightSearchCache(SearchFlightsHandlerTests.NewCache(), new RecordingLogger());
        var search = Search();

        (await searchCache.StoreAsync(search, TimeSpan.FromMinutes(30), TestContext.Current.CancellationToken)).ShouldBeTrue();

        var found = await searchCache.FindAsync(search.SearchId, TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found.Offers.ShouldHaveSingleItem().Offer.TotalPrice.ShouldBe(new Money(245.5m, new CurrencyCode("XTS")));
        found.Criteria.Origin.ShouldBe(new AirportCode("LHR"));
    }

    private static CachedFlightSearch Search()
    {
        var criteria = new FlightSearchCriteria(new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(1), CabinClass.Economy);
        var offer = new FlightOffer(
            new ProviderOfferRef("mock", "token"),
            new Money(245.5m, new CurrencyCode("XTS")),
            DateTimeOffset.UtcNow.AddMinutes(30),
            SliceSnapshotJson.Read(_storedVersion1));
        return new CachedFlightSearch(Guid.NewGuid(), criteria, [new CachedFlightOffer(Guid.NewGuid(), offer)]);
    }

    private sealed class RecordingLogger : ILogger<FlightSearchCache>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
