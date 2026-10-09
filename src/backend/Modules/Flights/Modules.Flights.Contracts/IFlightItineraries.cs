namespace TravelBooking.Modules.Flights.Contracts;

/// <summary>
/// What was selected (and booked) for an order item, for the customer's own booking page: the flights and the passenger
/// mix. A query for Orders by the selection id it holds; non-personal facts only, never supplier tokens.
/// </summary>
public interface IFlightItineraries
{
    /// <summary>The selection's itinerary, or null when there is none.</summary>
    Task<FlightItinerary?> GetItineraryAsync(Guid selectedOfferId, CancellationToken cancellationToken);
}

/// <param name="Legs">Outbound first, then the return for a round trip.</param>
/// <param name="Cabin">Economy, PremiumEconomy, Business or First.</param>
public sealed record FlightItinerary(IReadOnlyList<FlightItineraryLeg> Legs, string Cabin, int Adults, int Children, int Infants);

public sealed record FlightItineraryLeg(IReadOnlyList<FlightItinerarySegment> Segments);

/// <summary>
/// One flight. Departure and arrival are local times at their own airports (ADR 0010), never converted, with those
/// airports' IANA time zones when they are in the reference data (null otherwise).
/// </summary>
public sealed record FlightItinerarySegment(
    string MarketingCarrier, string FlightNumber, string Origin, string Destination, DateTime DepartureLocal, DateTime ArrivalLocal,
    string? OriginTimeZone, string? DestinationTimeZone);
