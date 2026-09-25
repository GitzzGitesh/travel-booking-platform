using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Flights.Ports;

/// <summary>
/// OUR stable reference for one supplier booking (ADR 0005: the order item id). It is sent to the supplier as its
/// client reference / idempotency token, and is the key for finding the booking again after an unknown outcome.
/// </summary>
public readonly record struct ClientReference
{
    public const int MaxLength = 64;

    public ClientReference(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"A client reference is 1 to {MaxLength} letters, digits or hyphens.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public override string ToString() => Value;
}

public enum PassengerType
{
    Adult,
    Child,
    Infant,
}

/// <summary>
/// A named traveller for a booking. Names are personal data (security rules): <see cref="ToString"/> never includes
/// them, so a passenger logged by mistake is redacted. Documents and contact details come with the traveller stories.
/// </summary>
public sealed record FlightPassenger
{
    public const int MaxNameLength = 60;

    public FlightPassenger(PassengerType type, string givenName, string familyName, DateOnly? dateOfBirth = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Undefined passenger type.");
        }

        Type = type;
        GivenName = RequireName(givenName, nameof(givenName));
        FamilyName = RequireName(familyName, nameof(familyName));
        DateOfBirth = dateOfBirth;
    }

    public PassengerType Type { get; }

    public string GivenName { get; }

    public string FamilyName { get; }

    public DateOnly? DateOfBirth { get; }

    public override string ToString() => $"{Type} passenger";

    private static string RequireName(string name, string parameter) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength
            ? name
            : throw new ArgumentException($"A passenger name is 1 to {MaxNameLength} characters.", parameter);
}

/// <summary>
/// Books a revalidated offer. <see cref="ExpectedTotalPrice"/> is the price the customer agreed to: a supplier price
/// that differs must not be booked (<c>PriceChanged</c>). Passengers match the searched passenger mix.
/// </summary>
public sealed record FlightBookingDetails(
    ClientReference ClientReference,
    ProviderOfferRef Offer,
    Money ExpectedTotalPrice,
    IReadOnlyList<FlightPassenger> Passengers);

/// <summary>The supplier's own booking locator (a PNR or order id): opaque to the core, stored verbatim.</summary>
public sealed record ProviderBookingRef(string ProviderId, string Value);

/// <summary>A booking that exists at the supplier, found by booking or by looking it up with our reference.</summary>
public sealed record FlightBookingConfirmation(ClientReference ClientReference, ProviderBookingRef Booking, Money TotalPrice);

/// <summary>A lookup by our reference: <see cref="Booking"/> is null when the supplier has definitely no such booking.</summary>
public sealed record FlightBookingLookup(FlightBookingConfirmation? Booking)
{
    public bool Found => Booking is not null;
}
