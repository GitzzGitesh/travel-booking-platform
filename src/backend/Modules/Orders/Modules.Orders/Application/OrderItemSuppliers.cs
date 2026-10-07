using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>Why a selection cannot be ordered or paid for now, whatever the product.</summary>
internal enum ItemUnavailable
{
    NotFound,
    NeedsPriceCheck,
    Expired,
    SoldOut,
    TryAgain,
    SupplierCannotBook,
}

/// <summary>A confirmed selection, whatever the product: the agreed price, its consent evidence, and the travellers it needs.</summary>
internal sealed record BookableItem(
    Guid SelectedOfferId, Money AgreedTotalPrice, DateTimeOffset OfferExpiresAt, PriceConsent? Consent, TravellerNeeds? Needs, bool DocumentsRequired,
    CancellationTerms? Terms = null);

/// <summary>
/// The selection side of each product's Contracts (ADR 0030 §6), by the item's product: Flights for a flight, Hotels for
/// a hotel stay. Never another module than the one that made the selection, and never a fallback between them.
/// </summary>
internal sealed class OrderItemSelections(IFlightSelections flights, IHotelSelections hotels)
{
    public async Task<Result<BookableItem, ItemUnavailable>> GetBookableAsync(OrderProduct product, Guid selectedOfferId, string customerId, CancellationToken cancellationToken) =>
        product is OrderProduct.Hotel
            ? Map(await hotels.GetBookableAsync(selectedOfferId, customerId, cancellationToken))
            : Map(await flights.GetBookableAsync(selectedOfferId, customerId, cancellationToken));

    public async Task<Result<BookableItem, ItemUnavailable>> RevalidateAsync(OrderProduct product, Guid selectedOfferId, string customerId, CancellationToken cancellationToken) =>
        product is OrderProduct.Hotel
            ? Map(await hotels.RevalidateAsync(selectedOfferId, customerId, cancellationToken))
            : Map(await flights.RevalidateAsync(selectedOfferId, customerId, cancellationToken));

    private static Result<BookableItem, ItemUnavailable> Map(Result<BookableFlightSelection, FlightSelectionUnavailable> result)
    {
        if (!result.IsSuccess)
        {
            return Result<BookableItem, ItemUnavailable>.Failure(result.Error switch
            {
                FlightSelectionUnavailable.NeedsPriceCheck => ItemUnavailable.NeedsPriceCheck,
                FlightSelectionUnavailable.Expired => ItemUnavailable.Expired,
                FlightSelectionUnavailable.SoldOut => ItemUnavailable.SoldOut,
                FlightSelectionUnavailable.TryAgain => ItemUnavailable.TryAgain,
                FlightSelectionUnavailable.SupplierCannotBook => ItemUnavailable.SupplierCannotBook,
                _ => ItemUnavailable.NotFound,
            });
        }

        var s = result.Value;
        return Result<BookableItem, ItemUnavailable>.Success(new BookableItem(
            s.SelectedOfferId,
            s.AgreedTotalPrice,
            s.OfferExpiresAt,
            Consent(s.AcceptedPriceQuoteId, s.PriceAcceptedAt),
            s.LastTravelDate is { } lastTravelDate ? new TravellerNeeds(s.Adults, s.Children, s.Infants, s.DocumentsRequired, lastTravelDate) : null,
            s.DocumentsRequired));
    }

    private static Result<BookableItem, ItemUnavailable> Map(Result<BookableHotelSelection, HotelSelectionUnavailable> result)
    {
        if (!result.IsSuccess)
        {
            return Result<BookableItem, ItemUnavailable>.Failure(result.Error switch
            {
                HotelSelectionUnavailable.NeedsPriceCheck => ItemUnavailable.NeedsPriceCheck,
                HotelSelectionUnavailable.Expired => ItemUnavailable.Expired,
                HotelSelectionUnavailable.SoldOut => ItemUnavailable.SoldOut,
                HotelSelectionUnavailable.TryAgain => ItemUnavailable.TryAgain,
                HotelSelectionUnavailable.SupplierCannotBook => ItemUnavailable.SupplierCannotBook,
                _ => ItemUnavailable.NotFound,
            });
        }

        var s = result.Value;
        // Guests are named, never documented: a hotel needs no travel documents (ADR 0030). Their data is kept until check-out.
        return Result<BookableItem, ItemUnavailable>.Success(new BookableItem(
            s.SelectedOfferId,
            s.AgreedTotalPrice,
            s.OfferExpiresAt,
            Consent(s.AcceptedPriceQuoteId, s.PriceAcceptedAt),
            new TravellerNeeds(s.Adults, s.Children, s.Infants, DocumentsRequired: false, s.CheckOut),
            DocumentsRequired: false,
            new CancellationTerms(s.Cancellation.Refundable, s.Cancellation.FreeCancellationUntil, s.Cancellation.PenaltyAfterDeadline?.Amount)));
    }

    private static PriceConsent? Consent(Guid? quote, DateTimeOffset? acceptedAt) =>
        quote is { } q && acceptedAt is { } at ? new PriceConsent(q, at) : null;
}
