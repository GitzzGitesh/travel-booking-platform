using System.Text;
using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Hotels.Contracts;

/// <summary>
/// Books a customer's confirmed hotel selection with its supplier, for the checkout (ADR 0005, ADR 0030): the one
/// synchronous supplier write another module may ask of Hotels. Exactly one supplier write per <see cref="BookAsync"/>
/// call, never retried here; an unknown outcome is settled only by <see cref="ReconcileAsync"/>, a lookup by our
/// reference. Supplier types, tokens and error codes never cross this contract (ADR 0004).
/// </summary>
public interface IHotelBookings
{
    /// <summary>
    /// Books the selection at the agreed price for these guests, under <see cref="HotelBookingRequest.ClientReference"/>
    /// (the order item id). Call it once per order item; after anything but Booked or NotBooked, only <see cref="ReconcileAsync"/>.
    /// </summary>
    Task<HotelBookingResult> BookAsync(HotelBookingRequest request, CancellationToken cancellationToken);

    /// <summary>Looks the booking up by our reference (a read, safe to repeat): Booked, NotFound as of now, Unknown or Mismatch.</summary>
    Task<HotelBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken);
}

public enum HotelBookingStatus
{
    /// <summary>The supplier holds the booking under our reference, at the agreed price.</summary>
    Booked,

    /// <summary>The supplier definitely did not book (a definitive refusal): nothing to reconcile.</summary>
    NotBooked,

    /// <summary>The outcome is unknown (timeout, ambiguous error, failed lookup): reconcile, never book again.</summary>
    Unknown,

    /// <summary>A lookup found no booking as of now; not conclusive by itself (supplier lookups can lag their writes).</summary>
    NotFound,

    /// <summary>A booking exists but not as agreed (price or supplier): a person decides; never charged.</summary>
    Mismatch,
}

/// <param name="ConfirmationNumber">The supplier's booking reference, when there is a booking (the property's own code may come later, F-51).</param>
/// <param name="Detail">Why nothing was booked (NotBooked), for the timeline: our wording, never the supplier's.</param>
public sealed record HotelBookingResult(HotelBookingStatus Status, string? ProviderId = null, string? ConfirmationNumber = null, string? Detail = null);

/// <summary>A booking request. Personal data: never printed.</summary>
/// <param name="ClientReference">Our reference for this booking: the order item id.</param>
/// <param name="AgreedPrice">The price the customer agreed to and is authorized for: a different supplier price is not booked.</param>
/// <param name="Guests">Every guest, the lead guest (an adult) first: the searched adults and children (by age).</param>
public sealed record HotelBookingRequest(
    Guid SelectedOfferId,
    string CustomerId,
    string ClientReference,
    Money AgreedPrice,
    IReadOnlyList<HotelBookingGuest> Guests,
    HotelBookingContact Contact,
    string? CorrelationId = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"SelectedOfferId = {SelectedOfferId}, ClientReference = {ClientReference}, AgreedPrice = {AgreedPrice}, Guests = {Guests.Count}");
        return true;
    }
}

/// <summary>
/// A guest as the property needs them: an adult (<paramref name="ChildAge"/> null), or a child with their age at check-out,
/// which must match the ages the stay was searched and priced for. Personal data: never printed.
/// </summary>
public sealed record HotelBookingGuest(string GivenNames, string Surname, int? ChildAge)
{
    public bool IsAdult => ChildAge is null;

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsAdult = {IsAdult}");
        return true;
    }
}

/// <summary>The booker's contact for the property. Personal data: never printed.</summary>
public sealed record HotelBookingContact(string Email, string Phone)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("[redacted]");
        return true;
    }
}
