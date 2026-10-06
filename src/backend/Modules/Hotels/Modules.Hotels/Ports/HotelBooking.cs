using System.Text;
using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Hotels.Ports;

/// <summary>A guest named on the booking: an adult, or a child of the stated age at check-out. Personal data: never printed.</summary>
public sealed record HotelGuest(string GivenNames, string Surname, int? ChildAge)
{
    public bool IsAdult => ChildAge is null;

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsAdult = {IsAdult}");
        return true;
    }
}

/// <summary>
/// Books a revalidated offer under OUR client reference (the order item id). <see cref="ExpectedTotalPrice"/> is the
/// price the customer agreed to: a supplier price that differs must not be booked (<c>PriceChanged</c>). The guests
/// are the searched occupancy (its adults, and its children with their ages), the lead guest (an adult) first.
/// </summary>
public sealed record HotelBookingDetails(
    string ClientReference,
    HotelOfferRef Offer,
    Money ExpectedTotalPrice,
    IReadOnlyList<HotelGuest> Guests,
    string ContactEmail,
    string ContactPhone)
{
    public const int MaxClientReferenceLength = 64;

    /// <summary>1 to 64 letters, digits or hyphens: what any supplier can take as a client reference.</summary>
    public static bool IsValidClientReference(string? value) =>
        value is { Length: > 0 and <= MaxClientReferenceLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Personal data: never printed.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ClientReference = {ClientReference}, ExpectedTotalPrice = {ExpectedTotalPrice}, Guests = {Guests.Count}");
        return true;
    }
}

/// <summary>A booking that exists at the supplier, found by booking or by looking it up with our reference.</summary>
/// <param name="ConfirmationNumber">The supplier's booking reference: opaque to the core, stored verbatim. The property's own confirmation code may come later (F-51).</param>
public sealed record HotelBookingConfirmation(string ClientReference, string ProviderId, string ConfirmationNumber, Money TotalPrice);

/// <summary>A lookup by our reference: <see cref="Booking"/> is null when the supplier has definitely no such booking.</summary>
public sealed record HotelBookingLookup(HotelBookingConfirmation? Booking);
