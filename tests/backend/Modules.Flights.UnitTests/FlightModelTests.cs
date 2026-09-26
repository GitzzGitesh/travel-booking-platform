using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Infrastructure.ReferenceData;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>The supplier-neutral fare model: a breakdown always adds up, and unstated facts stay unstated.</summary>
public sealed class FlightFareModelTests
{
    [Fact]
    public void A_breakdown_adds_up_per_passenger_type()
    {
        var breakdown = new FlightPriceBreakdown(
        [
            new PassengerFare(PassengerType.Adult, 2, Xts(100m), Xts(15m)),
            new PassengerFare(PassengerType.Child, 1, Xts(75m), Xts(10m)),
            new PassengerFare(PassengerType.Infant, 1, Xts(10m), Xts(1.5m)),
        ]);

        (breakdown.BaseFare, breakdown.TaxesAndFees, breakdown.Total).ShouldBe((Xts(285m), Xts(41.5m), Xts(326.5m)));
    }

    [Fact]
    public void A_breakdown_that_does_not_add_up_to_the_total_is_dropped_and_the_total_stays_the_price()
    {
        var breakdown = new FlightPriceBreakdown([new PassengerFare(PassengerType.Adult, 1, Xts(100m), Xts(15m))]);
        var fare = new FlightFare { PriceBreakdown = breakdown, ValidatingCarrier = "ZZ" };

        var inconsistent = Offer(Xts(115.01m)) with { Fare = fare };
        (inconsistent.ConsistentFare.PriceBreakdown, inconsistent.ConsistentFare.ValidatingCarrier).ShouldBe((null, "ZZ"));
        (Offer(Xts(115m)) with { Fare = fare }).ConsistentFare.ShouldBeSameAs(fare);
    }

    [Fact]
    public void A_selection_keeps_only_a_consistent_breakdown()
    {
        var breakdown = new FlightPriceBreakdown([new PassengerFare(PassengerType.Adult, 1, Xts(100m), Xts(15m))]);
        var offer = SelectedOfferStateTests.Current(270m) with { Fare = new FlightFare { PriceBreakdown = breakdown } };

        var selection = SelectedOffer.Select(Guid.NewGuid(), Guid.NewGuid(), SelectedOfferStateTests.Criteria(), offer, SelectedOfferStateTests.Now);

        selection.Fare.PriceBreakdown.ShouldBeNull();
    }

    [Fact]
    public void A_breakdown_is_in_one_currency_and_never_empty()
    {
        Should.Throw<ArgumentException>(() => new FlightPriceBreakdown([]));

        // Several entries for one type are allowed: children priced by age, or each passenger priced separately.
        new FlightPriceBreakdown([new PassengerFare(PassengerType.Child, 1, Xts(50m), Xts(5m)), new PassengerFare(PassengerType.Child, 1, Xts(60m), Xts(6m))])
            .Total.ShouldBe(Xts(121m));
        Should.Throw<ArgumentException>(() => new FlightPriceBreakdown(
            [new PassengerFare(PassengerType.Adult, 1, Xts(1m), Xts(1m)), new PassengerFare(PassengerType.Child, 1, Eur(1m), Eur(1m))]));
        Should.Throw<ArgumentException>(() => new PassengerFare(PassengerType.Adult, 1, Xts(1m), Eur(1m)));
        Should.Throw<ArgumentOutOfRangeException>(() => new PassengerFare(PassengerType.Adult, 0, Xts(1m), Xts(1m)));
    }

    [Fact]
    public void Baggage_counts_and_weights_are_never_negative()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BaggageAllowance(-1, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new BaggageAllowance(1, 1, 0));
        new BaggageAllowance(0, 1).CheckedBagMaxWeightKg.ShouldBeNull();
    }

    [Fact]
    public void An_offer_that_states_nothing_about_its_fare_says_so()
    {
        var fare = Offer(Xts(10m)).Fare;

        fare.ShouldBe(FlightFare.NotStated);
        (fare.Conditions.Refund, fare.Conditions.Change).ShouldBe((FareAllowance.NotStated, FareAllowance.NotStated));
    }

    [Fact]
    public void A_codeshare_leg_keeps_its_operating_carrier_apart_from_the_marketing_one()
    {
        var segment = new FlightSegment("ZZ", "ZZ100", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))
        {
            OperatingCarrier = "ZY",
        };

        (segment.MarketingCarrier, segment.OperatingCarrier).ShouldBe(("ZZ", "ZY"));
    }

    [Fact]
    public void An_enriched_offer_survives_the_search_cache_json_round_trip()
    {
        // HybridCache's distributed tier (ADR 0011) stores offers as JSON: every fact must come back, and validated.
        var breakdown = new FlightPriceBreakdown([new PassengerFare(PassengerType.Adult, 2, Xts(100m), Xts(15m))]);
        var segment = new FlightSegment("ZZ", "ZZ1", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))
        {
            OperatingCarrier = "ZY",
            Duration = TimeSpan.FromMinutes(135),
            FareBasis = "M1MOCK",
        };
        var offer = new FlightOffer(new ProviderOfferRef("stub", "token"), Xts(230m), new DateTimeOffset(2027, 1, 15, 9, 30, 0, TimeSpan.Zero), [new FlightSlice([segment])])
        {
            Fare = new FlightFare
            {
                PriceBreakdown = breakdown,
                ValidatingCarrier = "ZZ",
                Baggage = new BaggageAllowance(1, 1, 23),
                Conditions = new FareConditions(FareAllowance.AllowedWithFee, FareAllowance.Free),
                TicketingDeadline = new DateTimeOffset(2027, 1, 16, 9, 0, 0, TimeSpan.Zero),
            },
        };

        var read = System.Text.Json.JsonSerializer.Deserialize<FlightOffer>(System.Text.Json.JsonSerializer.Serialize(offer))!;

        read.Fare.PriceBreakdown!.Total.ShouldBe(Xts(230m));
        (read.Fare.ValidatingCarrier, read.Fare.Baggage, read.Fare.Conditions, read.Fare.TicketingDeadline)
            .ShouldBe((offer.Fare.ValidatingCarrier, offer.Fare.Baggage, offer.Fare.Conditions, offer.Fare.TicketingDeadline));
        var readSegment = read.Slices.Single().Segments.Single();
        (readSegment.OperatingCarrier, readSegment.Duration, readSegment.FareBasis).ShouldBe(("ZY", TimeSpan.FromMinutes(135), "M1MOCK"));
    }

    internal static FlightOffer Offer(Money total) =>
        new(new ProviderOfferRef("stub", "token"), total, new DateTimeOffset(2027, 1, 15, 9, 30, 0, TimeSpan.Zero), []);

    internal static Money Xts(decimal amount) => new(amount, new CurrencyCode("XTS"));

    private static Money Eur(decimal amount) => new(amount, new CurrencyCode("EUR"));
}

/// <summary>Airport reference data: names, and the IANA time zones local flight times are read in (ADR 0010).</summary>
public sealed class AirportDirectoryTests
{
    private static readonly EmbeddedAirportDirectory _airports = new();

    [Fact]
    public void The_embedded_dataset_loads_with_valid_zones()
    {
        _airports.Count.ShouldBeGreaterThanOrEqualTo(30);
        var heathrow = _airports.Find(new AirportCode("LHR")).ShouldNotBeNull();
        (heathrow.Name, heathrow.CityName, heathrow.CountryCode, heathrow.TimeZoneId).ShouldBe(("London Heathrow Airport", "London", "GB", "Europe/London"));
        _airports.Find(new AirportCode("DEL"))!.TimeZoneId.ShouldBe("Asia/Kolkata");
    }

    [Fact]
    public void An_airport_outside_the_dataset_is_not_found_rather_than_an_error() =>
        _airports.Find(new AirportCode("ZPC")).ShouldBeNull();

    [Fact]
    public void A_dataset_with_a_duplicate_or_an_unknown_zone_is_refused()
    {
        var lhr = new Airport(new AirportCode("LHR"), "Heathrow", "London", "GB", "Europe/London");

        Should.Throw<InvalidOperationException>(() => new EmbeddedAirportDirectory([lhr, lhr]));
        Should.Throw<ArgumentException>(() => new Airport(new AirportCode("XXX"), "Nowhere", "Nowhere", "GB", "Europe/Atlantis"));
        Should.Throw<ArgumentException>(() => new Airport(new AirportCode("XXX"), "Nowhere", "Nowhere", "gb", "Europe/London"));
    }

    [Fact]
    public void Local_times_are_read_in_the_airports_zone_with_daylight_saving()
    {
        var jfk = _airports.Find(new AirportCode("JFK"))!;

        jfk.ToInstant(new DateTime(2027, 1, 15, 12, 0, 0)).ShouldBe(new DateTimeOffset(2027, 1, 15, 12, 0, 0, TimeSpan.FromHours(-5)));
        jfk.ToInstant(new DateTime(2027, 7, 15, 12, 0, 0)).ShouldBe(new DateTimeOffset(2027, 7, 15, 12, 0, 0, TimeSpan.FromHours(-4)));
        jfk.ToLocal(new DateTimeOffset(2027, 1, 15, 17, 0, 0, TimeSpan.Zero)).ShouldBe(new DateTime(2027, 1, 15, 12, 0, 0));
    }

    [Fact]
    public void A_local_time_skipped_by_daylight_saving_does_not_exist()
    {
        // London moved from 01:00 to 02:00 on 28 March 2027.
        _airports.Find(new AirportCode("LHR"))!.ToInstant(new DateTime(2027, 3, 28, 1, 30, 0)).ShouldBeNull();
    }

    [Fact]
    public void Flying_time_comes_from_the_zones_when_the_supplier_does_not_state_it()
    {
        // London 10:00 GMT is 10:00 UTC; New York 12:55 EST is 17:55 UTC: 7 h 55 min, not the 2 h 55 min the clocks suggest.
        var segment = Segment("LHR", "JFK", new DateTime(2027, 2, 14, 10, 0, 0), new DateTime(2027, 2, 14, 12, 55, 0));

        ((IAirportDirectory)_airports).DurationOf(segment).ShouldBe(new TimeSpan(7, 55, 0));
    }

    [Fact]
    public void A_stated_flying_time_wins_and_an_unknown_airport_gives_none()
    {
        IAirportDirectory airports = _airports;
        var stated = Segment("LHR", "JFK", new DateTime(2027, 2, 14, 10, 0, 0), new DateTime(2027, 2, 14, 12, 55, 0)) with { Duration = TimeSpan.FromMinutes(480) };

        airports.DurationOf(stated).ShouldBe(TimeSpan.FromMinutes(480));
        airports.DurationOf(Segment("LHR", "ZPC", new DateTime(2027, 2, 14, 10, 0, 0), new DateTime(2027, 2, 14, 12, 0, 0))).ShouldBeNull();
    }

    private static FlightSegment Segment(string from, string to, DateTime departs, DateTime arrives) =>
        new("ZZ", "ZZ1", new AirportCode(from), new AirportCode(to), departs, arrives);
}

/// <summary>
/// Providers by id (ADR 0004): an offer is revalidated and booked by the provider that made it, whichever providers are
/// composed; an unknown id is never sent to another provider.
/// </summary>
public sealed class FlightProvidersTests
{
    private static readonly DateTimeOffset _now = SelectedOfferStateTests.Now;

    [Fact]
    public void Providers_are_found_by_id_and_an_unknown_id_by_none()
    {
        var a = new IdProvider("a");
        var providers = new FlightProviders([a, new IdProvider("b")]);

        providers.Find("a").ShouldBeSameAs(a);
        providers.Find("c").ShouldBeNull();
    }

    [Fact]
    public void Two_providers_with_one_id_are_a_composition_error() =>
        Should.Throw<InvalidOperationException>(() => new FlightProviders([new IdProvider("a"), new IdProvider("a")]));

    [Fact]
    public void Search_uses_the_one_composed_provider_and_is_not_fanned_out()
    {
        var only = new IdProvider("a");

        new FlightProviders([only]).ForSearch.ShouldBeSameAs(only);
        Should.Throw<InvalidOperationException>(() => new FlightProviders([]).ForSearch);
        Should.Throw<InvalidOperationException>(() => new FlightProviders([only, new IdProvider("b")]).ForSearch).Message.ShouldContain("not built");
    }

    [Fact]
    public void With_several_providers_search_uses_the_configured_one()
    {
        var real = new IdProvider("real");
        var providers = new FlightProviders([new IdProvider("mock"), real], searchProviderId: "real");

        providers.ForSearch.ShouldBeSameAs(real);
        providers.Find("mock").ShouldNotBeNull(); // offers the mock made are still revalidated by the mock
        Should.Throw<InvalidOperationException>(() => new FlightProviders([real], searchProviderId: "gone").ForSearch).Message.ShouldContain("gone");
    }

    [Fact]
    public async Task Revalidation_goes_to_the_offers_own_provider()
    {
        var offer = SelectedOfferStateTests.NewSelection(); // offered by "stub"
        var stub = new IdProvider("stub") { Offer = SelectedOfferStateTests.Current(270m, _now.AddMinutes(40)) };
        var other = new IdProvider("other");
        var handler = new RevalidateSelectedOfferHandler(new OneOfferStore(offer), new FlightProviders([other, stub]), new FakeTimeProvider(_now));

        (await handler.HandleAsync(offer.Id, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        (stub.Calls, other.Calls).ShouldBe((1, 0));
    }

    [Fact]
    public async Task Booking_goes_to_the_offers_own_provider_and_an_unknown_one_is_refused_unsent()
    {
        var other = new IdProvider("other");
        var booking = new FlightSupplierBooking(new FlightProviders([other]), new FakeTimeProvider(_now), NullLogger<FlightSupplierBooking>.Instance);
        var details = new FlightBookingDetails(new ClientReference("item-1"), new ProviderOfferRef("stub", "token"), FlightFareModelTests.Xts(270m), [new FlightPassenger(PassengerType.Adult, "Test", "Traveller")]);

        (await booking.BookAsync(details, TestContext.Current.CancellationToken)).ShouldBe(new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.InvalidRequest));
        (await booking.ReconcileAsync("stub", details.ClientReference, details.ExpectedTotalPrice, TestContext.Current.CancellationToken))
            .ShouldBe(new SupplierBookingOutcome.Unknown(ProviderErrorKind.Unavailable)); // cannot be looked up here: still unknown, never "not booked"
        other.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_selection_keeps_the_fare_facts_and_revalidation_refreshes_them()
    {
        var fare = new FlightFare { ValidatingCarrier = "ZZ", Baggage = new BaggageAllowance(1, 1, 23), TicketingDeadline = _now.AddDays(1) };
        var offer = SelectedOffer.Select(Guid.NewGuid(), Guid.NewGuid(), SelectedOfferStateTests.Criteria(), SelectedOfferStateTests.Current(270m) with { Fare = fare }, _now);
        offer.Fare.ShouldBe(fare);

        var later = fare with { TicketingDeadline = _now.AddHours(20) };
        offer.Revalidate(SelectedOfferStateTests.Current(270m) with { Fare = later }, _now.AddMinutes(1));

        offer.Fare.TicketingDeadline.ShouldBe(_now.AddHours(20));
    }

    private sealed class IdProvider(string id) : IFlightProvider
    {
        public FlightProviderCapabilities Capabilities => TestCapabilities.All;

        public FlightOffer? Offer { get; init; }

        public int Calls { get; private set; }

        public string Id => id;

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result<FlightOffer, ProviderError>.Success(Offer!));
        }

        public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken)
        {
            Calls++;
            throw new NotSupportedException();
        }

        public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken)
        {
            Calls++;
            throw new NotSupportedException();
        }
    }

    private sealed class OneOfferStore(SelectedOffer offer) : ISelectedOfferStore
    {
        public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindByIdAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult<SelectedOffer?>(selectedOfferId == offer.Id ? offer : null);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
