namespace TravelBooking.Modules.Customers.Contracts;

/// <summary>
/// What other modules may know about an order's travellers (ADR 0002: Contracts only). Facts, never personal data:
/// the names, dates of birth, contact details and documents stay in the Customers module (Q9, ADR 0020). Orders uses it
/// to refuse payment until the travellers are complete.
/// </summary>
public interface IOrderTravellers
{
    /// <summary>What has been provided for this customer's order; nothing provided is all zeros.</summary>
    Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken);
}

/// <param name="Adults">Travellers provided, by passenger type (with <paramref name="Children"/> and <paramref name="Infants"/>).</param>
/// <param name="ContactProvided">The booker's email and phone are provided.</param>
/// <param name="DocumentsProvided">How many of the travellers have a travel document stored.</param>
public sealed record OrderTravellersReadiness(int Adults, int Children, int Infants, bool ContactProvided, int DocumentsProvided);
