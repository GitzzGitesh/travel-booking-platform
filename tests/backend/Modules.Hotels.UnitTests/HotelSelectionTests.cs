using System.Globalization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Application;
using TravelBooking.Modules.Hotels.Domain;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.UnitTests;

/// <summary>
/// A hotel selection (ADR 0030): the same rules as a selected flight. Revalidation confirms the agreed price or quotes a
/// new one the customer must accept (F-01); expired and sold out are terminal (F-02, F-03).
/// </summary>
public sealed class HotelSelectionTests
{
    private static readonly DateTimeOffset _now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyCode _xts = new("XTS");
    private static readonly HotelSearchCriteria _criteria = new("PAR", new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 13), 2, [7]);

    [Fact]
    public void A_selection_keeps_the_stay_the_property_and_the_rate_as_selected()
    {
        var selection = Select(Offer(300m));

        (selection.Status, selection.Nights, selection.ChildAgeList.Single(), selection.AgreedPrice).ShouldBe((HotelSelectionStatus.Selected, 3, 7, Money(300m)));
        (selection.PropertyName, selection.Board, selection.Refundable).ShouldBe(("Hotel A", BoardBasis.Breakfast, true));
        selection.IsOwnedBy("cust-1").ShouldBeTrue();
        selection.IsVisibleTo("cust-2").ShouldBeFalse();
    }

    [Fact]
    public void Revalidating_at_the_same_price_confirms_and_a_new_price_is_a_quote_until_accepted()
    {
        var same = Select(Offer(300m));
        same.Revalidate(Offer(300m), _now);
        (same.Status, same.ConfirmedPrice).ShouldBe((HotelSelectionStatus.Confirmed, Money(300m)));

        var changed = Select(Offer(300m));
        changed.Revalidate(Offer(330m), _now);
        changed.Revalidate(Offer(330m), _now); // the same changed price keeps its quote
        var quote = changed.PriceQuoteId!.Value;
        (changed.Status, changed.QuotedPrice, changed.AgreedPrice).ShouldBe((HotelSelectionStatus.PriceChanged, Money(330m), Money(300m)));

        changed.AcceptPrice(Guid.NewGuid(), _now).Error.ShouldBe(HotelPriceAcceptanceFailure.StaleQuote);
        changed.AcceptPrice(quote, _now).IsSuccess.ShouldBeTrue();
        changed.AcceptPrice(quote, _now).IsSuccess.ShouldBeTrue(); // a replay
        (changed.Status, changed.AgreedPrice, changed.TotalPrice).ShouldBe((HotelSelectionStatus.Confirmed, Money(330m), Money(300m)));
    }

    [Fact]
    public void Expired_and_sold_out_are_terminal()
    {
        var expired = Select(Offer(300m));
        expired.MarkUnavailable(HotelSelectionStatus.Expired);
        expired.MarkUnavailable(HotelSelectionStatus.SoldOut);
        expired.Status.ShouldBe(HotelSelectionStatus.Expired);
        Should.Throw<InvalidOperationException>(() => expired.Revalidate(Offer(300m), _now));
        expired.AcceptPrice(Guid.NewGuid(), _now).Error.ShouldBe(HotelPriceAcceptanceFailure.OfferExpired);
        Should.Throw<ArgumentOutOfRangeException>(() => Select(Offer(300m)).MarkUnavailable(HotelSelectionStatus.Confirmed));
    }

    [Fact]
    public void An_expired_offer_cannot_be_selected_and_another_providers_offer_never_revalidates_it()
    {
        Should.Throw<InvalidOperationException>(() => HotelSelection.Select(Guid.NewGuid(), Guid.NewGuid(), _criteria, Offer(300m) with { ExpiresAt = _now }, _now, null));
        var selection = Select(Offer(300m));
        Should.Throw<InvalidOperationException>(() => selection.Revalidate(Offer(300m) with { Reference = new HotelOfferRef("other", "t") }, _now));
    }

    [Theory]
    [InlineData(true, true, null, true)] // refundable, the whole price after the deadline
    [InlineData(true, true, "50", true)]
    [InlineData(true, true, "301", false)] // a penalty above the price
    [InlineData(true, false, null, false)] // refundable without a deadline
    [InlineData(false, false, null, true)] // non-refundable
    [InlineData(false, true, null, false)] // non-refundable with a deadline
    public void Cancellation_terms_must_be_stated_consistently(bool refundable, bool deadline, string? penalty, bool wellFormed) =>
        new CancellationPolicy(refundable, deadline ? _now.AddDays(5) : null, penalty is null ? null : Money(decimal.Parse(penalty, System.Globalization.CultureInfo.InvariantCulture)))
            .IsWellFormed(Money(300m)).ShouldBe(wellFormed);

    [Theory]
    [InlineData("PAR", 2, 2, true)]
    [InlineData("par", 2, 2, false)]
    [InlineData("PAR", 2, 0, false)] // no night
    [InlineData("PAR", 2, 31, false)] // more than 30 nights
    [InlineData("PAR", 5, 2, false)] // more than four adults
    [InlineData("PAR", 0, 2, false)] // no adult
    public void A_search_is_one_room_of_1_to_30_nights(string destination, int adults, int nights, bool wellFormed) =>
        new HotelSearchCriteria(destination, new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 10).AddDays(nights), adults, []).IsWellFormed().ShouldBe(wellFormed);

    [Fact]
    public async Task A_search_refuses_dates_outside_the_horizon_and_drops_offers_it_cannot_sell_as_stated()
    {
        var clock = new FakeTimeProvider(_now);
        var provider = new StubProvider(clock);
        var handler = Handler(provider, clock);

        (await handler.HandleAsync(_criteria with { CheckIn = new DateOnly(2027, 2, 20), CheckOut = new DateOnly(2027, 2, 22) }, Ct)).Error
            .ShouldBeOfType<SearchHotelsFailure.InvalidDates>().Field.ShouldBe("CheckIn");
        (await handler.HandleAsync(_criteria with { CheckIn = new DateOnly(2028, 3, 5), CheckOut = new DateOnly(2028, 3, 6) }, Ct)).IsSuccess.ShouldBeFalse();

        var found = await handler.HandleAsync(_criteria, Ct);
        found.Value.Offers.Count.ShouldBe(1); // the well-formed one; the inconsistent and expired ones are dropped
        found.Value.Offers[0].Offer.Property.Name.ShouldBe("Hotel A");
    }

    [Fact]
    public void Changed_board_room_or_cancellation_terms_at_the_same_price_are_a_quote_never_a_silent_confirmation()
    {
        var selection = Select(Offer(300m));

        selection.Revalidate(Offer(300m) with { Cancellation = CancellationPolicy.NonRefundable }, _now.AddMinutes(1));
        selection.Status.ShouldBe(HotelSelectionStatus.PriceChanged);
        selection.TermsChanged.ShouldBeTrue();
        selection.QuotedPrice.ShouldBe(Money(300m));
        var quote = selection.PriceQuoteId!.Value;

        // The same changed terms again: the same quote. Different terms again: a new quote.
        selection.Revalidate(Offer(300m) with { Cancellation = CancellationPolicy.NonRefundable }, _now.AddMinutes(2));
        selection.PriceQuoteId.ShouldBe(quote);
        selection.Revalidate(Offer(300m) with { Cancellation = CancellationPolicy.NonRefundable, Board = BoardBasis.RoomOnly }, _now.AddMinutes(3));
        selection.PriceQuoteId.ShouldNotBe(quote);
        selection.AcceptPrice(quote, _now.AddMinutes(4)).Error.ShouldBe(HotelPriceAcceptanceFailure.StaleQuote);

        selection.AcceptPrice(selection.PriceQuoteId!.Value, _now.AddMinutes(4)).IsSuccess.ShouldBeTrue();
        selection.Status.ShouldBe(HotelSelectionStatus.Confirmed);
        selection.TermsChanged.ShouldBeFalse();
        selection.Cancellation.ShouldBe(CancellationPolicy.NonRefundable);
        selection.Board.ShouldBe(BoardBasis.RoomOnly);

        // Accepted terms at the agreed price confirm; another room description is a quote again.
        selection.Revalidate(Offer(300m) with { Cancellation = CancellationPolicy.NonRefundable, Board = BoardBasis.RoomOnly }, _now.AddMinutes(5));
        selection.Status.ShouldBe(HotelSelectionStatus.Confirmed);
        selection.Revalidate(Offer(300m) with { Cancellation = CancellationPolicy.NonRefundable, Board = BoardBasis.RoomOnly, RoomDescription = "Twin room" }, _now.AddMinutes(6));
        selection.Status.ShouldBe(HotelSelectionStatus.PriceChanged);
        selection.TermsChanged.ShouldBeTrue();
    }

    [Theory]
    [InlineData("p-1", "PAR", "FR", "4", "Europe/Paris", true)]
    [InlineData("p-1", "PAR", "FR", null, "Europe/Paris", true)]
    [InlineData("p-1", "PAR", "FR", "3.5", "Europe/Paris", true)]
    [InlineData("p-1", "PAR", "FR", "3.7", "Europe/Paris", false)]
    [InlineData("p-1", "PAR", "FR", "6", "Europe/Paris", false)]
    [InlineData("p-1", "PAR", "FR", "-1", "Europe/Paris", false)]
    [InlineData("p-1", "par", "FR", "4", "Europe/Paris", false)]
    [InlineData("p-1", "PARI", "FR", "4", "Europe/Paris", false)]
    [InlineData("p-1", "PAR", "FRA", "4", "Europe/Paris", false)]
    [InlineData("p-1", "PAR", "FR", "4", "Mars/Olympus", false)]
    [InlineData("", "PAR", "FR", "4", "Europe/Paris", false)]
    public void A_property_must_fit_what_we_store_and_show(string id, string city, string country, string? stars, string zone, bool wellFormed) =>
        new HotelProperty(id, "Hotel A", "1 Street", city, country, stars is null ? null : decimal.Parse(stars, CultureInfo.InvariantCulture), zone)
            .IsWellFormed().ShouldBe(wellFormed);

    [Fact]
    public void A_property_id_longer_than_its_column_is_refused_never_truncated()
    {
        Offer(300m).Property.IsWellFormed().ShouldBeTrue();
        (Offer(300m).Property with { Id = new string('x', HotelProperty.MaxIdLength) }).IsWellFormed().ShouldBeTrue();
        (Offer(300m).Property with { Id = new string('x', HotelProperty.MaxIdLength + 1) }).IsWellFormed().ShouldBeFalse();
    }

    [Fact]
    public async Task A_revalidation_returning_another_property_ends_the_selection_and_a_malformed_offer_is_a_provider_failure()
    {
        var clock = new FakeTimeProvider(_now);
        var store = new SingleSelectionStore(Select(Offer(300m)));
        var provider = new FixedRevalidation();
        var handler = new RevalidateHotelSelectionHandler(store, new HotelProviders([provider]), clock);

        provider.Next = Offer(300m) with { Property = Offer(300m).Property with { StarRating = 9m } };
        (await handler.HandleAsync(store.Selection.Id, "cust-1", Ct)).Error.ShouldBeOfType<HotelSelectionFailure.ProviderFailed>();
        store.Selection.Status.ShouldBe(HotelSelectionStatus.Selected);

        provider.Next = Offer(300m) with { Property = Offer(300m).Property with { Id = "p-2" } };
        (await handler.HandleAsync(store.Selection.Id, "cust-1", Ct)).Error.ShouldBeOfType<HotelSelectionFailure.SoldOut>();
        store.Selection.Status.ShouldBe(HotelSelectionStatus.SoldOut);
        store.Selection.PropertyId.ShouldBe("p-1");
    }

    [Fact]
    public void Only_a_confirmed_selection_is_booked_and_a_booked_one_is_frozen()
    {
        var selection = Select(Offer(300m));
        selection.MarkBooked().ShouldBeFalse(); // not confirmed yet
        selection.Revalidate(Offer(300m), _now.AddMinutes(1));

        selection.StartBooking().ShouldBeTrue();
        selection.StartBooking().ShouldBeFalse(); // once
        selection.IsFrozen.ShouldBeTrue();
        RevalidateHotelSelectionHandler.Unavailable(selection).ShouldBeOfType<HotelSelectionFailure.Booked>(); // a price check while booking: refused
        selection.AcceptPrice(Guid.NewGuid(), _now.AddMinutes(1)).Error.ShouldBe(HotelPriceAcceptanceFailure.Booked);

        selection.MarkBooked().ShouldBeTrue();
        selection.MarkBooked().ShouldBeFalse(); // again: a no-op
        selection.Status.ShouldBe(HotelSelectionStatus.Booked);
        selection.IsAvailable.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => selection.Revalidate(Offer(300m) with { Board = BoardBasis.RoomOnly }, _now.AddMinutes(2)));
        selection.AcceptPrice(Guid.NewGuid(), _now.AddMinutes(2)).Error.ShouldBe(HotelPriceAcceptanceFailure.Booked);
        selection.Board.ShouldBe(BoardBasis.Breakfast);
        RevalidateHotelSelectionHandler.Unavailable(selection).ShouldBeOfType<HotelSelectionFailure.Booked>();

        var soldOut = Select(Offer(300m));
        soldOut.MarkUnavailable(HotelSelectionStatus.SoldOut);
        (soldOut.StartBooking(), soldOut.MarkBooked()).ShouldBe((false, false)); // never from an unavailable selection
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Money Money(decimal amount) => new(amount, _xts);

    private static HotelSelection Select(HotelOffer offer) => HotelSelection.Select(Guid.NewGuid(), Guid.NewGuid(), _criteria, offer, _now, "cust-1");

    private static HotelOffer Offer(decimal total) => new(
        new HotelOfferRef("stub", $"token-{total}"),
        new HotelProperty("p-1", "Hotel A", "1 Street", "PAR", "FR", 4m, "Europe/Paris"),
        "Double room",
        BoardBasis.Breakfast,
        Money(total),
        null,
        CancellationPolicy.FreeUntil(_now.AddDays(30), Money(100m)),
        _now.AddMinutes(30));

    private static SearchHotelsHandler Handler(IHotelProvider provider, TimeProvider clock)
    {
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new SearchHotelsHandler(new HotelProviders([provider]), new HotelSearchCache(cache, NullLogger<HotelSearchCache>.Instance), clock,
            NullLogger<SearchHotelsHandler>.Instance);
    }

    private sealed class StubProvider(TimeProvider clock) : IHotelProvider
    {
        public string Id => "stub";

        public Task<Result<HotelSearchResult, ProviderError>> SearchAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken) =>
            Task.FromResult(Result<HotelSearchResult, ProviderError>.Success(new HotelSearchResult(
            [
                Offer(300m),
                Offer(200m) with { Cancellation = new CancellationPolicy(true, null, null) }, // refundable without a deadline
                Offer(250m) with { ExpiresAt = clock.GetUtcNow() }, // already expired
                Offer(260m) with { Property = Offer(260m).Property with { Id = new string('x', HotelProperty.MaxIdLength + 1) } }, // an id we cannot store
                Offer(270m) with { Property = Offer(270m).Property with { TimeZone = "Not/AZone" } }, // a zone we cannot show deadlines in
            ])));

        public Task<Result<HotelOffer, ProviderError>> RevalidateAsync(HotelOfferRef offer, CancellationToken cancellationToken) =>
            Task.FromResult(Result<HotelOffer, ProviderError>.Success(Offer(300m)));

        public Task<Result<HotelBookingConfirmation, ProviderError>> BookAsync(HotelBookingDetails details, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<HotelBookingLookup, ProviderError>> RetrieveBookingAsync(string clientReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedRevalidation : IHotelProvider
    {
        public HotelOffer Next { get; set; } = Offer(300m);

        public string Id => "stub";

        public Task<Result<HotelSearchResult, ProviderError>> SearchAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<HotelOffer, ProviderError>> RevalidateAsync(HotelOfferRef offer, CancellationToken cancellationToken) =>
            Task.FromResult(Result<HotelOffer, ProviderError>.Success(Next));

        public Task<Result<HotelBookingConfirmation, ProviderError>> BookAsync(HotelBookingDetails details, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<HotelBookingLookup, ProviderError>> RetrieveBookingAsync(string clientReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SingleSelectionStore(HotelSelection selection) : IHotelSelectionStore
    {
        public HotelSelection Selection => selection;

        public Task<HotelSelection?> FindAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken) =>
            Task.FromResult<HotelSelection?>(selection);

        public Task<bool> TryAddAsync(HotelSelection added, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HotelSelection?> FindForUpdateAsync(Guid selectionId, CancellationToken cancellationToken) =>
            Task.FromResult<HotelSelection?>(selectionId == selection.Id ? selection : null);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<HotelSelection?> FindByIdAsync(Guid selectionId, CancellationToken cancellationToken) => FindForUpdateAsync(selectionId, cancellationToken);
    }
}
