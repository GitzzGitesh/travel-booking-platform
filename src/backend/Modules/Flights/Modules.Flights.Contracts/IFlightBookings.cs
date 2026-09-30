using System.Text;
using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Flights.Contracts;

/// <summary>
/// Books a customer's confirmed flight selection with its supplier, for the checkout (ADR 0005, ADR 0021): the one
/// synchronous supplier write another module may ask of Flights. Exactly one supplier write per <see cref="BookAsync"/>
/// call, never retried here; an unknown outcome is settled only by <see cref="ReconcileAsync"/>, a lookup by our
/// reference. Supplier types, tokens and error codes never cross this contract (ADR 0004).
/// </summary>
public interface IFlightBookings
{
    /// <summary>
    /// Books the selection at the agreed price for these passengers, under <see cref="FlightBookingRequest.ClientReference"/>
    /// (the order item id: the supplier's idempotency token wherever it supports one). Call it once per order item; after
    /// anything but Booked or NotBooked, only <see cref="ReconcileAsync"/>.
    /// </summary>
    Task<FlightBookingResult> BookAsync(FlightBookingRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Looks the booking up by our reference (a read, safe to repeat): Booked, NotFound as of now (conclusive only after
    /// the supplier's consistency window), Unknown (the lookup failed), or Mismatch.
    /// </summary>
    Task<FlightBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken);
}

public enum FlightBookingStatus
{
    /// <summary>The supplier holds the booking under our reference, at the agreed price.</summary>
    Booked,

    /// <summary>The supplier definitely did not book (a definitive refusal): nothing to reconcile.</summary>
    NotBooked,

    /// <summary>The outcome is unknown (timeout, ambiguous error, failed lookup): reconcile, never book again.</summary>
    Unknown,

    /// <summary>A lookup found no booking as of now; not conclusive by itself (supplier lookups can lag their writes).</summary>
    NotFound,

    /// <summary>A booking exists but not as agreed (price, reference or supplier): a person decides; never charged.</summary>
    Mismatch,
}

/// <summary>Whether the supplier has issued the tickets: Pending until it says so.</summary>
public enum FlightTicketingStatus
{
    Pending,
    Issued,
}

/// <param name="ProviderId">The supplier that holds (or was asked for) the booking.</param>
/// <param name="Locator">The supplier's booking locator (PNR or order id), when there is a booking.</param>
/// <param name="Detail">Why nothing was booked (NotBooked), for the timeline: our wording, never the supplier's.</param>
public sealed record FlightBookingResult(
    FlightBookingStatus Status, string? ProviderId = null, string? Locator = null, FlightTicketingStatus Ticketing = FlightTicketingStatus.Pending, string? Detail = null);

public enum FlightPassengerKind
{
    Adult,
    Child,
    Infant,
}

public enum FlightPassengerGender
{
    Female,
    Male,
}

public enum FlightTravelDocumentKind
{
    Passport,
    IdentityCard,
}

/// <summary>A booking request. Personal data: never printed (<see cref="PrintMembers"/>).</summary>
/// <param name="ClientReference">Our reference for this booking: the order item id.</param>
/// <param name="AgreedPrice">The price the customer agreed to and is authorized for: a different supplier price is not booked.</param>
public sealed record FlightBookingRequest(
    Guid SelectedOfferId,
    string CustomerId,
    string ClientReference,
    Money AgreedPrice,
    IReadOnlyList<FlightBookingPassenger> Passengers,
    FlightBookingContact Contact,
    string? CorrelationId = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"SelectedOfferId = {SelectedOfferId}, ClientReference = {ClientReference}, AgreedPrice = {AgreedPrice}, Passengers = {Passengers.Count}");
        return true;
    }
}

/// <summary>A traveller as the supplier needs them (Q9). Personal data: never printed.</summary>
public sealed record FlightBookingPassenger(
    FlightPassengerKind Kind, string GivenNames, string Surname, DateOnly DateOfBirth, FlightPassengerGender Gender, FlightBookingDocument? Document)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Document = {(Document is null ? "none" : "[redacted]")}");
        return true;
    }
}

/// <summary>A travel document, only when the supplier requires one (Q9). Sensitive personal data: never printed.</summary>
public sealed record FlightBookingDocument(FlightTravelDocumentKind Kind, string Number, string IssuingCountry, string Nationality, DateOnly ExpiryDate)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}

/// <summary>The booker's contact for the supplier (Q9). Personal data: never printed.</summary>
public sealed record FlightBookingContact(string Email, string Phone)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("[redacted]");
        return true;
    }
}
