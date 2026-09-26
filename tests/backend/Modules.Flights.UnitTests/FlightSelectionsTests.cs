using Microsoft.Extensions.Time.Testing;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>What Orders may see of a selection (Flights.Contracts): only Confirmed and unexpired is bookable.</summary>
public sealed class FlightSelectionsTests
{
    private readonly FakeTimeProvider _clock = new(SelectedOfferStateTests.Now);

    [Fact]
    public async Task A_confirmed_unexpired_selection_is_bookable_at_its_agreed_price_without_consent_fields()
    {
        var offer = SelectedOfferStateTests.NewSelection();
        offer.Revalidate(SelectedOfferStateTests.Current(270m), SelectedOfferStateTests.Now);

        var result = await Selections(offer).GetBookableAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new BookableFlightSelection(offer.Id, SelectedOfferStateTests.Selected, offer.OfferExpiresAt, null, null));
    }

    [Fact]
    public async Task An_accepted_price_change_is_bookable_with_its_consent_evidence()
    {
        var offer = SelectedOfferStateTests.NewSelection();
        offer.Revalidate(SelectedOfferStateTests.Current(310.5m), SelectedOfferStateTests.Now);
        var quote = offer.PriceQuoteId!.Value;
        offer.AcceptPrice(quote, SelectedOfferStateTests.Now.AddMinutes(1));

        var result = await Selections(offer).GetBookableAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Value.AgreedTotalPrice.ShouldBe(SelectedOfferStateTests.Xts(310.5m));
        result.Value.AcceptedPriceQuoteId.ShouldBe(quote);
        result.Value.PriceAcceptedAt.ShouldBe(SelectedOfferStateTests.Now.AddMinutes(1));
    }

    [Fact]
    public async Task F02_a_confirmed_selection_past_its_offer_expiry_is_expired()
    {
        var offer = SelectedOfferStateTests.NewSelection();
        offer.Revalidate(SelectedOfferStateTests.Current(270m), SelectedOfferStateTests.Now);
        _clock.Advance(TimeSpan.FromMinutes(30));

        var result = await Selections(offer).GetBookableAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(FlightSelectionUnavailable.Expired);
    }

    [Fact]
    public async Task Unrevalidated_or_price_changed_selections_need_a_price_check()
    {
        var selected = SelectedOfferStateTests.NewSelection();
        var changed = SelectedOfferStateTests.NewSelection();
        changed.Revalidate(SelectedOfferStateTests.Current(310.5m), SelectedOfferStateTests.Now);

        (await Selections(selected).GetBookableAsync(selected.Id, TestContext.Current.CancellationToken)).Error.ShouldBe(FlightSelectionUnavailable.NeedsPriceCheck);
        (await Selections(changed).GetBookableAsync(changed.Id, TestContext.Current.CancellationToken)).Error.ShouldBe(FlightSelectionUnavailable.NeedsPriceCheck);
    }

    [Theory]
    [InlineData(SelectedOfferStatus.Expired, FlightSelectionUnavailable.Expired)]
    [InlineData(SelectedOfferStatus.SoldOut, FlightSelectionUnavailable.SoldOut)]
    internal async Task Unavailable_selections_are_reported(SelectedOfferStatus status, FlightSelectionUnavailable expected)
    {
        var offer = SelectedOfferStateTests.NewSelection();
        offer.MarkUnavailable(status);

        (await Selections(offer).GetBookableAsync(offer.Id, TestContext.Current.CancellationToken)).Error.ShouldBe(expected);
    }

    [Fact]
    public async Task An_unknown_selection_is_not_found() =>
        (await Selections(SelectedOfferStateTests.NewSelection()).GetBookableAsync(Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Error.ShouldBe(FlightSelectionUnavailable.NotFound);

    // GetBookableAsync reads the store only: the revalidation handler is never called here.
    private FlightSelections Selections(SelectedOffer offer) =>
        new(new SingleOfferStore(offer), new RevalidateSelectedOfferHandler(new SingleOfferStore(offer), new FlightProviders([]), _clock), new FlightProviders([]), _clock);

    private sealed class SingleOfferStore(SelectedOffer offer) : ISelectedOfferStore
    {
        public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindByIdAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult(selectedOfferId == offer.Id ? offer : null);

        public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
