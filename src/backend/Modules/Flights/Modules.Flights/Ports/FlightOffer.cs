using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Flights.Ports;

/// <summary>
/// The provider's reference for an offer. <see cref="Value"/> is an OPAQUE token owned by the adapter: it may be long
/// and may carry whatever the supplier needs to revalidate or book the offer. The core persists it verbatim with the
/// offer snapshot and never parses it (ADR 0014).
/// </summary>
public sealed record ProviderOfferRef(string ProviderId, string Value);

/// <summary>
/// A priced itinerary for the searched passengers. It expires, and must be revalidated before payment
/// (booking rules). <see cref="ExpiresAt"/> is an instant (UTC). The price breakdown, fare conditions, and baggage
/// are added with the pricing and offer-selection stories.
/// </summary>
public sealed record FlightOffer(ProviderOfferRef Reference, Money TotalPrice, DateTimeOffset ExpiresAt, IReadOnlyList<FlightSlice> Slices);

/// <summary>One origin-to-destination journey (outbound or return), possibly with connections.</summary>
public sealed record FlightSlice(IReadOnlyList<FlightSegment> Segments);

/// <summary>
/// One flight leg. Departure and arrival are LOCAL times at their own airports, kept as supplied and never converted
/// for storage (ADR 0010). They are in different time zones, so arrival can be "earlier" than departure.
/// </summary>
public sealed record FlightSegment(
    string MarketingCarrier,
    string FlightNumber,
    AirportCode Origin,
    AirportCode Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal);
