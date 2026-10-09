using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>What one order item booked: its flights, or its hotel stay (each null when the product's module has none).</summary>
internal sealed record BookedItemDetails(FlightItinerary? Flight, HotelStay? Hotel);

/// <summary>The customer's own order, its latest cancellation request, and what each of its items booked.</summary>
internal sealed record CustomerOrderDetails(Order Order, CancellationRequest? Cancellation, IReadOnlyDictionary<Guid, BookedItemDetails> Booked);

/// <summary>
/// The customer's own order page (QA BUG-002): the order, found only for its owner, with what each item booked, read
/// through the product modules' query contracts (ADR 0002, ADR 0015: queries). Never used for order lists.
/// </summary>
internal sealed class CustomerOrderDetailsQuery(
    IOrderStore store, ICancellationRequestStore cancellations, IFlightItineraries flights, IHotelStays hotels)
{
    public async Task<CustomerOrderDetails?> GetOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken)
    {
        if (await store.FindOwnedAsync(orderId, customerId, cancellationToken) is not { } order)
        {
            return null;
        }

        var booked = new Dictionary<Guid, BookedItemDetails>();
        foreach (var item in order.Items)
        {
            booked[item.Id] = item.Product is OrderProduct.Hotel
                ? new BookedItemDetails(null, await hotels.GetStayAsync(item.SelectedOfferId, cancellationToken))
                : new BookedItemDetails(await flights.GetItineraryAsync(item.SelectedOfferId, cancellationToken), null);
        }

        return new CustomerOrderDetails(order, await cancellations.FindLatestForOrderAsync(order.Id, cancellationToken), booked);
    }
}
