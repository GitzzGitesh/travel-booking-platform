using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>
/// The Flights side of <see cref="IFlightSelections"/>: <see cref="GetBookableAsync"/> reads the stored selection only;
/// <see cref="RevalidateAsync"/> asks the supplier first.
/// </summary>
internal sealed class FlightSelections(ISelectedOfferStore store, RevalidateSelectedOfferHandler revalidation, FlightProviders providers, TimeProvider timeProvider) : IFlightSelections
{
    public async Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
        Bookable(await store.FindByIdAsync(selectedOfferId, cancellationToken));

    public async Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, CancellationToken cancellationToken)
    {
        // Checkout asks this right before payment: an offer its supplier cannot book is refused before any money moves.
        if (await store.FindByIdAsync(selectedOfferId, cancellationToken) is { } selected
            && (providers.FindFor(selected.ProviderId, ProviderOperation.Book) is null || providers.FindFor(selected.ProviderId, ProviderOperation.RetrieveBooking) is null))
        {
            return Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(FlightSelectionUnavailable.SupplierCannotBook);
        }

        var revalidated = await revalidation.HandleAsync(selectedOfferId, cancellationToken);
        if (revalidated.IsSuccess)
        {
            return Bookable(revalidated.Value);
        }

        return Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(revalidated.Error switch
        {
            SelectedOfferFailure.NotFound => FlightSelectionUnavailable.NotFound,
            SelectedOfferFailure.PriceChanged => FlightSelectionUnavailable.NeedsPriceCheck,
            SelectedOfferFailure.OfferExpired => FlightSelectionUnavailable.Expired,
            SelectedOfferFailure.SoldOut => FlightSelectionUnavailable.SoldOut,
            _ => FlightSelectionUnavailable.TryAgain, // Conflict or ProviderFailed: nothing changed
        });
    }

    private Result<BookableFlightSelection, FlightSelectionUnavailable> Bookable(SelectedOffer? offer)
    {
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
