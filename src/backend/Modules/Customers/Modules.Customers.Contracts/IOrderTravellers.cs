using System.Text;

namespace TravelBooking.Modules.Customers.Contracts;

/// <summary>
/// What other modules may know about an order's travellers (ADR 0002: Contracts only). Readiness is facts, never
/// personal data: Orders uses it to refuse payment until the travellers are complete. The travellers themselves are
/// released only for the supplier booking (<see cref="GetForBookingAsync"/>), never stored outside Customers (Q9, ADR 0020).
/// </summary>
public interface IOrderTravellers
{
    /// <summary>What has been provided for this customer's order; nothing provided is all zeros.</summary>
    Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken);

    /// <summary>
    /// The travellers, contact and any stored documents of this customer's order, for its supplier booking only: every
    /// document read is audited (actor <c>system:flight-booking</c>). Null when they are incomplete, anonymised, another
    /// customer's, or a document cannot be read (then nothing may be sent to the supplier). Never log or store the result.
    /// </summary>
    Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>Personal data for one supplier booking: never printed.</summary>
public sealed record BookingTravellers(string Email, string Phone, IReadOnlyList<BookingTraveller> Travellers)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Travellers = {Travellers.Count}");
        return true;
    }
}

/// <summary>A traveller for the supplier booking, in order position. Personal data: never printed.</summary>
public sealed record BookingTraveller(
    TravellerType Type, string GivenNames, string Surname, DateOnly DateOfBirth, TravellerGenderType Gender, BookingTravelDocument? Document)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Type = {Type}");
        return true;
    }
}

/// <summary>A decrypted travel document for the supplier booking only. Sensitive personal data: never printed or stored.</summary>
public sealed record BookingTravelDocument(TravelDocumentKind Kind, string Number, string IssuingCountry, string Nationality, DateOnly ExpiryDate)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}

/// <param name="Adults">Travellers provided, by passenger type (with <paramref name="Children"/> and <paramref name="Infants"/>).</param>
/// <param name="ContactProvided">The booker's email and phone are provided.</param>
/// <param name="DocumentsProvided">How many of the travellers have a travel document stored.</param>
public sealed record OrderTravellersReadiness(int Adults, int Children, int Infants, bool ContactProvided, int DocumentsProvided);
