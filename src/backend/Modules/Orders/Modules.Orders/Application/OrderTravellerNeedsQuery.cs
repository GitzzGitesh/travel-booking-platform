using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>
/// <see cref="IOrderTravellerNeeds"/>: the customer's own order only (another customer's is not found), and only when
/// its needs were recorded. Travellers may change while the order awaits payment and no payment attempt is live: once
/// funds are (or may be) held for them, they are what the booking uses. A declined attempt frees them again.
/// </summary>
internal sealed class OrderTravellerNeedsQuery(IOrderStore store, IOrderPayments payments) : IOrderTravellerNeeds
{
    public async Task<OrderTravellerNeeds?> FindAsync(Guid orderId, string customerId, CancellationToken cancellationToken)
    {
        if (!Order.IsValidCustomerId(customerId) || await store.FindOwnedAsync(orderId, customerId, cancellationToken) is not { } order
            || order.Items.Select(i => i.TravellerNeeds).FirstOrDefault(n => n is { IsKnown: true }) is not { } needs)
        {
            return null;
        }

        return new OrderTravellerNeeds(
            order.Id,
            needs.Adults,
            needs.Children,
            needs.Infants,
            order.Items.Any(i => i.TravellerNeeds is { DocumentsRequired: true }),
            order.Items.Select(i => i.TravellerNeeds?.LastTravelDate ?? needs.LastTravelDate).Max(),
            order.Status is OrderStatus.AwaitingPayment && await payments.FindLiveAsync(order.Id, cancellationToken) is null);
    }
}
