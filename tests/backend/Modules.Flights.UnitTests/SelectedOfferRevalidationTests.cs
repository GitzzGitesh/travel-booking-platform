using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>The selected offer's revalidation state machine (F-01..F-03): every legal and illegal transition.</summary>
public sealed class SelectedOfferStateTests
{
    internal static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    internal static readonly Money Selected = Xts(270m);

    [Fact]
    public void A_new_selection_is_Selected_at_its_searched_price()
    {
        var offer = NewSelection();

        offer.Status.ShouldBe(SelectedOfferStatus.Selected);
        offer.AgreedPrice.ShouldBe(Selected);
        offer.ConfirmedPrice.ShouldBeNull();
        offer.QuotedPrice.ShouldBeNull();
    }

    [Fact]
    public void The_same_price_confirms_the_offer_and_takes_the_new_expiry()
    {
        var offer = NewSelection();

        offer.Revalidate(Current(270.00m, expires: Now.AddMinutes(45)), Now);

        offer.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        offer.ConfirmedPrice.ShouldBe(Selected);
        offer.OfferExpiresAt.ShouldBe(Now.AddMinutes(45));
        offer.RevalidatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(310.5)]
    [InlineData(199.99)] // a lower price must also be shown and accepted, never applied silently
    public void A_different_price_is_quoted_without_changing_the_selected_price(decimal amount)
    {
        var offer = NewSelection();

        offer.Revalidate(Current(amount), Now);

        offer.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
        offer.QuotedPrice.ShouldBe(Xts(amount));
        offer.PriceQuoteId.ShouldNotBeNull();
        offer.TotalPrice.ShouldBe(Selected);
        offer.AgreedPrice.ShouldBe(Selected);
        offer.ConfirmedPrice.ShouldBeNull();
    }

    [Fact]
    public void A_different_currency_is_a_price_change()
    {
        var offer = NewSelection();

        offer.Revalidate(Current(270m) with { TotalPrice = new Money(270m, new CurrencyCode("EUR")) }, Now);

        offer.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
    }

    [Fact]
    public void Accepting_the_current_quote_confirms_the_new_price_and_is_idempotent()
    {
        var offer = PriceChangedTo(310.5m, out var quote);

        var accepted = offer.AcceptPrice(quote, Now);
        var replay = offer.AcceptPrice(quote, Now);

        accepted.IsSuccess.ShouldBeTrue();
        replay.IsSuccess.ShouldBeTrue();
        offer.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        offer.ConfirmedPrice.ShouldBe(Xts(310.5m));
        offer.AgreedPrice.ShouldBe(Xts(310.5m));
        offer.TotalPrice.ShouldBe(Selected);
        offer.QuotedPrice.ShouldBeNull();
        offer.AcceptedPriceQuoteId.ShouldBe(quote);
    }

    [Fact]
    public void Another_quote_id_is_stale()
    {
        var offer = PriceChangedTo(310.5m, out _);

        offer.AcceptPrice(Guid.NewGuid(), Now).Error.ShouldBe(AcceptPriceFailure.StaleQuote);
        offer.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
    }

    [Fact]
    public void There_is_nothing_to_accept_before_a_price_change()
    {
        var offer = NewSelection();

        offer.AcceptPrice(Guid.NewGuid(), Now).Error.ShouldBe(AcceptPriceFailure.StaleQuote);
        offer.Status.ShouldBe(SelectedOfferStatus.Selected);
    }

    [Fact]
    public void The_same_changed_price_keeps_its_quote_so_another_tab_can_still_accept_it()
    {
        var offer = PriceChangedTo(310.5m, out var quote);

        offer.Revalidate(Current(310.5m), Now);

        offer.PriceQuoteId.ShouldBe(quote);
        offer.AcceptPrice(quote, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_revalidated_reference_replaces_the_searched_one()
    {
        var offer = NewSelection();

        offer.Revalidate(Current(270m) with { Reference = new ProviderOfferRef("stub", "stub-token-2") }, Now);

        offer.ProviderOfferToken.ShouldBe("stub-token-2");
    }

    [Fact]
    public void An_offer_from_another_provider_cannot_revalidate_this_selection() =>
        Should.Throw<InvalidOperationException>(() =>
            NewSelection().Revalidate(Current(270m) with { Reference = new ProviderOfferRef("other", "x") }, Now));

    [Theory]
    [InlineData(SelectedOfferStatus.Selected)]
    [InlineData(SelectedOfferStatus.PriceChanged)]
    [InlineData(SelectedOfferStatus.Confirmed)]
    internal void Available_is_not_ready_to_book_only_Confirmed_is(SelectedOfferStatus status)
    {
        var offer = status switch
        {
            SelectedOfferStatus.PriceChanged => PriceChangedTo(310.5m, out _),
            SelectedOfferStatus.Confirmed => Confirmed(),
            _ => NewSelection(),
        };

        offer.IsAvailable.ShouldBeTrue();
        offer.Status.ShouldBe(status);
    }

    [Fact]
    public void A_quote_superseded_by_a_second_change_cannot_be_accepted()
    {
        var offer = PriceChangedTo(310.5m, out var first);

        offer.Revalidate(Current(320m), Now);

        offer.AcceptPrice(first, Now).Error.ShouldBe(AcceptPriceFailure.StaleQuote);
        offer.QuotedPrice.ShouldBe(Xts(320m));
    }

    [Fact]
    public void A_price_that_returns_to_the_agreed_price_confirms_and_drops_the_quote()
    {
        var offer = PriceChangedTo(310.5m, out var quote);

        offer.Revalidate(Current(270m), Now);

        offer.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        offer.QuotedPrice.ShouldBeNull();
        offer.AcceptPrice(quote, Now).Error.ShouldBe(AcceptPriceFailure.StaleQuote);
    }

    [Fact]
    public void A_confirmed_offer_is_compared_with_its_confirmed_price_when_revalidated_again()
    {
        var offer = PriceChangedTo(310.5m, out var quote);
        offer.AcceptPrice(quote, Now);

        offer.Revalidate(Current(310.5m), Now);
        offer.Status.ShouldBe(SelectedOfferStatus.Confirmed);

        offer.Revalidate(Current(330m), Now);
        offer.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
        offer.AgreedPrice.ShouldBe(Xts(310.5m));
    }

    [Fact]
    public void A_quote_on_an_offer_that_has_since_expired_cannot_be_accepted()
    {
        var offer = PriceChangedTo(310.5m, out var quote);

        offer.AcceptPrice(quote, Now.AddMinutes(31)).Error.ShouldBe(AcceptPriceFailure.OfferExpired);
        offer.Status.ShouldBe(SelectedOfferStatus.Expired);
        offer.ConfirmedPrice.ShouldBeNull();
    }

    [Theory]
    [InlineData(SelectedOfferStatus.Expired, AcceptPriceFailure.OfferExpired)]
    [InlineData(SelectedOfferStatus.SoldOut, AcceptPriceFailure.SoldOut)]
    internal void Expired_and_sold_out_are_terminal(SelectedOfferStatus reason, AcceptPriceFailure failure)
    {
        var offer = PriceChangedTo(310.5m, out var quote);

        offer.MarkUnavailable(reason);
        offer.MarkUnavailable(SelectedOfferStatus.Expired); // no-op once unavailable

        offer.Status.ShouldBe(reason);
        offer.QuotedPrice.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => offer.Revalidate(Current(270m), Now));
        offer.AcceptPrice(quote, Now).Error.ShouldBe(failure);
    }

    [Theory]
    [InlineData(SelectedOfferStatus.Selected)]
    [InlineData(SelectedOfferStatus.Confirmed)]
    [InlineData(SelectedOfferStatus.PriceChanged)]
    internal void Only_expired_or_sold_out_make_an_offer_unavailable(SelectedOfferStatus reason) =>
        Should.Throw<ArgumentOutOfRangeException>(() => NewSelection().MarkUnavailable(reason));

    internal static Money Xts(decimal amount) => new(amount, new CurrencyCode("XTS"));

    internal static SelectedOffer NewSelection() =>
        SelectedOffer.Select(Guid.NewGuid(), Guid.NewGuid(), Criteria(), Current(270m), Now.AddMinutes(-5));

    internal static FlightOffer Current(decimal amount, DateTimeOffset? expires = null) => new(
        new ProviderOfferRef("stub", "stub-token-1"),
        Xts(amount),
        expires ?? Now.AddMinutes(30),
        [new FlightSlice([new FlightSegment("ZZ", "ZZ123", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))])]);

    private static FlightSearchCriteria Criteria() =>
        new(new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(1), CabinClass.Economy);

    private static SelectedOffer Confirmed()
    {
        var offer = NewSelection();
        offer.Revalidate(Current(270m), Now);
        return offer;
    }

    private static SelectedOffer PriceChangedTo(decimal amount, out Guid quote)
    {
        var offer = NewSelection();
        offer.Revalidate(Current(amount), Now);
        quote = offer.PriceQuoteId!.Value;
        return offer;
    }
}

public sealed class RevalidateSelectedOfferHandlerTests
{
    private readonly FakeTimeProvider _clock = new(SelectedOfferStateTests.Now);

    [Fact]
    public async Task The_stored_opaque_token_is_revalidated_and_a_matching_price_is_confirmed()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        var provider = new StubProvider(Success(270m));

        var result = await Handler(store, provider).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Value.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        provider.Revalidated.ShouldHaveSingleItem().ShouldBe(new ProviderOfferRef("stub", "stub-token-1"));
        store.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task F01_a_changed_price_is_returned_as_a_quote()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());

        var result = await Handler(store, new StubProvider(Success(310.5m))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        var changed = result.Error.ShouldBeOfType<SelectedOfferFailure.PriceChanged>().Offer;
        changed.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
        changed.QuotedPrice.ShouldBe(SelectedOfferStateTests.Xts(310.5m));
        changed.TotalPrice.ShouldBe(SelectedOfferStateTests.Selected);
    }

    [Fact]
    public async Task F02_an_offer_past_its_expiry_is_expired_without_calling_the_supplier()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        var provider = new StubProvider(Success(270m));
        _clock.Advance(TimeSpan.FromMinutes(31));

        var result = await Handler(store, provider).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.OfferExpired>();
        provider.Revalidated.ShouldBeEmpty();
        offer.Status.ShouldBe(SelectedOfferStatus.Expired);
        store.Saves.ShouldBe(1);
    }

    [Theory]
    [InlineData(ProviderErrorKind.OfferExpired, SelectedOfferStatus.Expired)]
    [InlineData(ProviderErrorKind.SoldOut, SelectedOfferStatus.SoldOut)]
    internal async Task F02_and_F03_from_the_supplier_are_recorded_and_terminal(ProviderErrorKind kind, SelectedOfferStatus status)
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        var provider = new StubProvider(Failure(kind));

        var first = await Handler(store, provider).HandleAsync(offer.Id, TestContext.Current.CancellationToken);
        var again = await Handler(store, provider).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        offer.Status.ShouldBe(status);
        first.Error.GetType().ShouldBe(again.Error.GetType());
        provider.Revalidated.Count.ShouldBe(1); // an unavailable offer is not sent to the supplier again
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unavailable)]
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.InvalidRequest)]
    internal async Task Supplier_failures_change_nothing(ProviderErrorKind kind)
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());

        var result = await Handler(store, new StubProvider(Failure(kind))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.ProviderFailed>().Error.Kind.ShouldBe(kind);
        offer.Status.ShouldBe(SelectedOfferStatus.Selected);
        store.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task An_offer_whose_provider_is_not_configured_fails_without_becoming_terminal()
    {
        var offer = SelectedOffer.Select(Guid.NewGuid(), Guid.NewGuid(), Criteria(), SelectedOfferStateTests.Current(270m) with { Reference = new ProviderOfferRef("retired", "t") }, SelectedOfferStateTests.Now.AddMinutes(-5));
        var (store, _) = StoreWith(offer);
        var provider = new StubProvider(Success(270m));

        var result = await Handler(store, provider).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.ProviderFailed>().Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
        offer.Status.ShouldBe(SelectedOfferStatus.Selected);
        provider.Revalidated.ShouldBeEmpty();
        store.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task F02_an_offer_the_supplier_returns_already_expired_is_expired()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        var expired = Result<FlightOffer, ProviderError>.Success(SelectedOfferStateTests.Current(270m, SelectedOfferStateTests.Now.AddSeconds(-1)));

        var result = await Handler(store, new StubProvider(expired)).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.OfferExpired>();
        offer.Status.ShouldBe(SelectedOfferStatus.Expired);
    }

    [Fact]
    public async Task F01_the_handler_reports_a_saved_price_change_for_the_customer_to_accept()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());

        var result = await Handler(store, new StubProvider(Success(310.5m))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.PriceChanged>().Offer.ShouldBeSameAs(offer);
        store.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task A_concurrent_change_while_accepting_is_a_conflict()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        await Handler(store, new StubProvider(Success(310.5m))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);
        store.SaveSucceeds = false;

        var result = await new AcceptSelectedOfferPriceHandler(store, _clock).HandleAsync(offer.Id, offer.PriceQuoteId!.Value, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.Conflict>();
    }

    [Fact]
    public async Task An_unknown_selection_is_not_found()
    {
        var (store, _) = StoreWith(SelectedOfferStateTests.NewSelection());

        var result = await Handler(store, new StubProvider(Success(270m))).HandleAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.NotFound>();
    }

    [Fact]
    public async Task A_concurrent_change_is_a_conflict()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        store.SaveSucceeds = false;

        var result = await Handler(store, new StubProvider(Success(270m))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.Conflict>();
    }

    [Fact]
    public async Task Accepting_the_quote_confirms_it_and_a_stale_quote_is_rejected()
    {
        var (store, offer) = StoreWith(SelectedOfferStateTests.NewSelection());
        await Handler(store, new StubProvider(Success(310.5m))).HandleAsync(offer.Id, TestContext.Current.CancellationToken);
        var accept = new AcceptSelectedOfferPriceHandler(store, _clock);

        var stale = await accept.HandleAsync(offer.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);
        var accepted = await accept.HandleAsync(offer.Id, offer.PriceQuoteId!.Value, TestContext.Current.CancellationToken);

        stale.Error.ShouldBeOfType<SelectedOfferFailure.StaleQuote>();
        accepted.Value.AgreedPrice.ShouldBe(SelectedOfferStateTests.Xts(310.5m));
        (await accept.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Error.ShouldBeOfType<SelectedOfferFailure.NotFound>();
    }

    private static FlightSearchCriteria Criteria() =>
        new(new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(1), CabinClass.Economy);

    private RevalidateSelectedOfferHandler Handler(ISelectedOfferStore store, IFlightProvider provider) => new(store, provider, _clock);

    private static (FakeStore Store, SelectedOffer Offer) StoreWith(SelectedOffer offer) => (new FakeStore(offer), offer);

    private static Result<FlightOffer, ProviderError> Success(decimal amount) =>
        Result<FlightOffer, ProviderError>.Success(SelectedOfferStateTests.Current(amount, SelectedOfferStateTests.Now.AddMinutes(40)));

    private static Result<FlightOffer, ProviderError> Failure(ProviderErrorKind kind) =>
        Result<FlightOffer, ProviderError>.Failure(new ProviderError(kind, "stub"));

    private sealed class FakeStore(SelectedOffer offer) : ISelectedOfferStore
    {
        public int Saves { get; private set; }

        public bool SaveSucceeds { get; set; } = true;

        public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult(selectedOfferId == offer.Id ? offer : null);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            if (SaveSucceeds)
            {
                Saves++;
            }

            return Task.FromResult(SaveSucceeds);
        }
    }

    private sealed class StubProvider(Result<FlightOffer, ProviderError> result) : IFlightProvider
    {
        public List<ProviderOfferRef> Revalidated { get; } = [];

        public string Id => "stub";

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken)
        {
            Revalidated.Add(offer);
            return Task.FromResult(result);
        }
    }
}
