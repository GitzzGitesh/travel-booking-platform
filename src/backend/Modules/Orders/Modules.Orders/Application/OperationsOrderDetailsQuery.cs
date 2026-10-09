using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>A hotel item and the stay it booked.</summary>
internal sealed record OperationsHotelStay(OrderItem Item, HotelStay Stay);

/// <summary>Any order, for operations: its latest cancellation request and its hotel items' stays.</summary>
internal sealed record OperationsOrderDetails(Order Order, CancellationRequest? Cancellation, IReadOnlyList<OperationsHotelStay> Stays);

/// <summary>
/// The operations order page: the order with what operations need to handle it with a property's desk (ADR 0030),
/// read through the Hotels query contract (ADR 0002, ADR 0015: queries). Non-personal facts only.
/// </summary>
internal sealed class OperationsOrderDetailsQuery(IOrderStore store, ICancellationRequestStore cancellations, IHotelStays hotels)
{
    public async Task<OperationsOrderDetails?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(orderId, cancellationToken) is not { } order)
        {
            return null;
        }

        var stays = new List<OperationsHotelStay>();
        foreach (var item in order.Items.Where(i => i.Product is OrderProduct.Hotel))
        {
            if (await hotels.GetStayAsync(item.SelectedOfferId, cancellationToken) is { } stay)
            {
                stays.Add(new OperationsHotelStay(item, stay));
            }
        }

        return new OperationsOrderDetails(order, await cancellations.FindLatestForOrderAsync(order.Id, cancellationToken), stays);
    }
}
