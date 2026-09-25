using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.ProviderContracts.Flights;

/// <summary>
/// Search and revalidation semantics every <see cref="IFlightProvider"/> must honour, mocks included (ADR 0004: mock parity).
/// An implementation inherits this class and supplies a provider plus its clock. Real adapters run it against
/// their sandbox (nightly); mocks run it on every PR. Only assert what is true of real flights: local times are in
/// different zones, so arrival is NOT required to be after departure here.
/// </summary>
public abstract class FlightProviderSearchContract
{
    protected abstract IFlightProvider Provider { get; }

    protected abstract DateTimeOffset Now { get; }

    protected FlightSearchCriteria OneWay(PassengerMix? passengers = null, CabinClass cabin = CabinClass.Economy) =>
        new(new AirportCode("LHR"), new AirportCode("JFK"), Today.AddDays(30), null, passengers ?? new PassengerMix(1), cabin);

    protected FlightSearchCriteria RoundTrip() =>
        new(new AirportCode("LHR"), new AirportCode("JFK"), Today.AddDays(30), Today.AddDays(37), new PassengerMix(2, 1, 1), CabinClass.Economy);

    private DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task One_way_offers_match_the_criteria()
    {
        var criteria = OneWay();

        var offers = await SearchOffers(criteria);

        offers.ShouldNotBeEmpty();
        foreach (var offer in offers)
        {
            offer.Slices.Count.ShouldBe(1);
            AssertSlice(offer.Slices[0], criteria.Origin, criteria.Destination, criteria.DepartureDate);
        }
    }

    [Fact]
    public async Task Round_trip_offers_have_an_outbound_and_a_return_slice()
    {
        var criteria = RoundTrip();

        var offers = await SearchOffers(criteria);

        offers.ShouldNotBeEmpty();
        foreach (var offer in offers)
        {
            offer.Slices.Count.ShouldBe(2);
            AssertSlice(offer.Slices[0], criteria.Origin, criteria.Destination, criteria.DepartureDate);
            AssertSlice(offer.Slices[1], criteria.Destination, criteria.Origin, criteria.ReturnDate!.Value);
        }
    }

    [Fact]
    public async Task Offers_are_priced_unexpired_and_uniquely_referenced()
    {
        var offers = await SearchOffers(OneWay());

        offers.ShouldAllBe(o => o.TotalPrice.Amount > 0 && o.TotalPrice.Currency.Value.Length == 3);
        offers.ShouldAllBe(o => o.ExpiresAt > Now);
        offers.ShouldAllBe(o => o.Reference.ProviderId == Provider.Id && !string.IsNullOrWhiteSpace(o.Reference.Value));
        offers.Select(o => o.Reference.Value).ShouldBeUnique();
    }

    [Fact]
    public async Task Differently_priced_searches_never_share_offer_references()
    {
        var economy = await SearchOffers(OneWay());
        var business = await SearchOffers(OneWay(cabin: CabinClass.Business));
        var twoAdults = await SearchOffers(OneWay(new PassengerMix(2)));

        var references = economy.Concat(business).Concat(twoAdults).Select(o => o.Reference.Value).ToList();
        references.ShouldBeUnique();
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider.SearchAsync(OneWay(), cancelled.Token));
    }

    [Fact]
    public async Task Revalidating_a_fresh_offer_returns_the_same_itinerary_priced_and_unexpired()
    {
        var offer = (await SearchOffers(RoundTrip()))[0];

        var result = await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? string.Empty : $"Revalidation failed: {result.Error}");
        result.Value.Reference.ProviderId.ShouldBe(Provider.Id);
        result.Value.TotalPrice.Amount.ShouldBeGreaterThan(0);
        result.Value.ExpiresAt.ShouldBeGreaterThan(Now);
        Itinerary(result.Value).ShouldBe(Itinerary(offer));
    }

    [Fact]
    public async Task Revalidation_is_a_repeatable_read()
    {
        var offer = (await SearchOffers(OneWay()))[0];

        var first = await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken);
        var second = await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken);

        second.IsSuccess.ShouldBe(first.IsSuccess);
        second.Value.TotalPrice.ShouldBe(first.Value.TotalPrice);
    }

    [Fact]
    public async Task An_unrecognised_reference_is_an_error_not_an_exception()
    {
        var result = await Provider.RevalidateAsync(new ProviderOfferRef(Provider.Id, "not-an-offer-reference"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBeOneOf(ProviderErrorKind.InvalidRequest, ProviderErrorKind.OfferExpired);
    }

    [Fact]
    public async Task Revalidation_honours_cancellation()
    {
        var offer = (await SearchOffers(OneWay()))[0];
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider.RevalidateAsync(offer.Reference, cancelled.Token));
    }

    [Fact]
    public async Task Booking_a_revalidated_offer_is_found_again_by_our_reference()
    {
        var (request, _) = await BookableOffer();

        var booked = await Provider.BookAsync(request, TestContext.Current.CancellationToken);
        var lookup = await Provider.RetrieveBookingAsync(request.ClientReference, TestContext.Current.CancellationToken);

        booked.IsSuccess.ShouldBeTrue(booked.IsSuccess ? string.Empty : $"Booking failed: {booked.Error}");
        booked.Value.ClientReference.ShouldBe(request.ClientReference);
        booked.Value.Booking.ProviderId.ShouldBe(Provider.Id);
        booked.Value.Booking.Value.ShouldNotBeNullOrWhiteSpace();
        booked.Value.TotalPrice.ShouldBe(request.ExpectedTotalPrice);
        lookup.Value.Booking.ShouldNotBeNull().Booking.ShouldBe(booked.Value.Booking);
    }

    [Fact]
    public async Task Booking_again_with_the_same_reference_never_creates_a_second_booking()
    {
        var (request, _) = await BookableOffer();

        var first = await Provider.BookAsync(request, TestContext.Current.CancellationToken);
        var second = await Provider.BookAsync(request, TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        if (second.IsSuccess)
        {
            second.Value.Booking.ShouldBe(first.Value.Booking);
        }

        var lookup = await Provider.RetrieveBookingAsync(request.ClientReference, TestContext.Current.CancellationToken);
        lookup.Value.Booking.ShouldNotBeNull().Booking.ShouldBe(first.Value.Booking);
    }

    [Fact]
    public async Task Parallel_bookings_with_one_reference_make_exactly_one_booking()
    {
        var (request, _) = await BookableOffer();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider.BookAsync(request, TestContext.Current.CancellationToken)));

        var lookup = await Provider.RetrieveBookingAsync(request.ClientReference, TestContext.Current.CancellationToken);
        var booking = lookup.Value.Booking.ShouldNotBeNull().Booking;
        results.Where(r => r.IsSuccess).ShouldAllBe(r => r.Value.Booking == booking);
    }

    [Fact]
    public async Task The_same_reference_with_other_details_never_books_the_new_details()
    {
        var (request, _) = await BookableOffer();
        var first = await Provider.BookAsync(request, TestContext.Current.CancellationToken);
        var (other, _) = await BookableOffer();

        var replay = await Provider.BookAsync(other with { ClientReference = request.ClientReference, Passengers = [Passenger(PassengerType.Adult, "Someone")] }, TestContext.Current.CancellationToken);

        if (replay.IsSuccess)
        {
            replay.Value.Booking.ShouldBe(first.Value.Booking);
            replay.Value.TotalPrice.ShouldBe(first.Value.TotalPrice);
        }
    }

    [Fact]
    public async Task A_booking_at_a_price_other_than_the_agreed_one_is_not_made()
    {
        var (request, _) = await BookableOffer();
        var wrongPrice = request with { ExpectedTotalPrice = request.ExpectedTotalPrice with { Amount = request.ExpectedTotalPrice.Amount + 1m } };

        var booked = await Provider.BookAsync(wrongPrice, TestContext.Current.CancellationToken);

        booked.IsSuccess.ShouldBeFalse();
        booked.Error.Kind.ShouldBe(ProviderErrorKind.PriceChanged);
        (await Provider.RetrieveBookingAsync(request.ClientReference, TestContext.Current.CancellationToken)).Value.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task Passengers_that_do_not_match_the_offer_are_an_invalid_request()
    {
        var (request, _) = await BookableOffer();
        var extraAdult = request with { Passengers = [.. request.Passengers, Passenger(PassengerType.Adult, "Second")] };

        var booked = await Provider.BookAsync(extraAdult, TestContext.Current.CancellationToken);

        booked.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Looking_up_an_unknown_reference_is_a_definite_not_found()
    {
        var lookup = await Provider.RetrieveBookingAsync(NewReference(), TestContext.Current.CancellationToken);

        lookup.IsSuccess.ShouldBeTrue(lookup.IsSuccess ? string.Empty : $"Lookup failed: {lookup.Error}");
        lookup.Value.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task Booking_and_lookup_honour_cancellation()
    {
        var (request, _) = await BookableOffer();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider.BookAsync(request, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Provider.RetrieveBookingAsync(request.ClientReference, cancelled.Token));
    }

    /// <summary>A freshly searched and revalidated one-adult offer, with a booking request at its current price.</summary>
    protected async Task<(FlightBookingDetails Request, FlightOffer Offer)> BookableOffer(string familyName = "Traveller")
    {
        var offer = (await SearchOffers(OneWay()))[0];
        var current = (await Provider.RevalidateAsync(offer.Reference, TestContext.Current.CancellationToken)).Value;
        return (new FlightBookingDetails(NewReference(), current.Reference, current.TotalPrice, [Passenger(PassengerType.Adult, familyName)]), current);
    }

    // Synthetic travellers only (testing rules: no real PII).
    protected static FlightPassenger Passenger(PassengerType type, string familyName) => new(type, "Test", familyName);

    protected static ClientReference NewReference() => new($"test-{Guid.NewGuid():N}");

    protected static string Itinerary(FlightOffer offer) =>
        string.Join(" | ", offer.Slices.Select(slice =>
            string.Join(" ", slice.Segments.Select(s => $"{s.FlightNumber} {s.Origin}-{s.Destination} {s.DepartureLocal:O} {s.ArrivalLocal:O}"))));

    protected async Task<IReadOnlyList<FlightOffer>> SearchOffers(FlightSearchCriteria criteria)
    {
        var result = await Provider.SearchAsync(criteria, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? string.Empty : $"Search failed: {result.Error}");
        return result.Value.Offers;
    }

    private static void AssertSlice(FlightSlice slice, AirportCode from, AirportCode to, DateOnly date)
    {
        var segments = slice.Segments;
        segments.ShouldNotBeEmpty();
        segments[0].Origin.ShouldBe(from);
        segments[^1].Destination.ShouldBe(to);
        segments.Zip(segments.Skip(1)).ShouldAllBe(pair => pair.First.Destination == pair.Second.Origin);
        DateOnly.FromDateTime(segments[0].DepartureLocal).ShouldBe(date);
        segments.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.FlightNumber));
        segments.ShouldAllBe(s => s.DepartureLocal.Kind == DateTimeKind.Unspecified && s.ArrivalLocal.Kind == DateTimeKind.Unspecified);
    }
}
