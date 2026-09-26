using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>The Flights side of <see cref="IFlightSelections"/>: reads the stored selection, never the supplier.</summary>
internal sealed class FlightSelections(ISelectedOfferStore store, TimeProvider timeProvider) : IFlightSelections
{
    public async Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, CancellationToken cancellationToken)
    {
        var offer = await store.FindByIdAsync(selectedOfferId, cancellationToken);
        FlightSelectionUnavailable? unavailable = offer switch
        {
            null => FlightSelectionUnavailable.NotFound,
            { Status: SelectedOfferStatus.SoldOut } => FlightSelectionUnavailable.SoldOut,
            { Status: SelectedOfferStatus.Expired } => FlightSelectionUnavailable.Expired,
            { Status: not SelectedOfferStatus.Confirmed } => FlightSelectionUnavailable.NeedsPriceCheck,
            _ when offer.OfferExpiresAt <= timeProvider.GetUtcNow() => FlightSelectionUnavailable.Expired,
            _ => null,
        };

        return unavailable is { } reason
            ? Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(reason)
            : Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(new BookableFlightSelection(
                offer!.Id, offer.AgreedPrice, offer.OfferExpiresAt, offer.AcceptedPriceQuoteId, offer.PriceAcceptedAt));
    }
}
