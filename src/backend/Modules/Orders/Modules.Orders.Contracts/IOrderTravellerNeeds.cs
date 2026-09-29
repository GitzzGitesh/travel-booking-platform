namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>
/// What an order needs from its travellers (a query, ADR 0002): the Customers module uses it to validate and keep the
/// travellers' personal data, and to date its retention (Q9). The customer id comes from a validated token only.
/// </summary>
public interface IOrderTravellerNeeds
{
    /// <summary>The needs of this customer's own order; null if it does not exist, is someone else's, or predates them.</summary>
    Task<OrderTravellerNeeds?> FindAsync(Guid orderId, string customerId, CancellationToken cancellationToken);
}

/// <param name="Adults">The passenger mix to provide travellers for (with <paramref name="Children"/> and <paramref name="Infants"/>).</param>
/// <param name="DocumentsRequired">The supplier requires travel documents: only then are they collected (Q9).</param>
/// <param name="LastTravelDate">The local date of the last arrival: personal data is kept until a period after it (Q9).</param>
/// <param name="Editable">Travellers may still be given or changed: the order awaits payment.</param>
public sealed record OrderTravellerNeeds(Guid OrderId, int Adults, int Children, int Infants, bool DocumentsRequired, DateOnly LastTravelDate, bool Editable)
{
    public int Travellers => Adults + Children + Infants;
}
