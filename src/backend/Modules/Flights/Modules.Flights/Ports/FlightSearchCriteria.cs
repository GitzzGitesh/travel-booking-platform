namespace TravelBooking.Modules.Flights.Ports;

public enum CabinClass
{
    Economy,
    PremiumEconomy,
    Business,
    First,
}

/// <summary>A one-way (no return date) or round-trip search. Dates are local to the departure airport.</summary>
public sealed record FlightSearchCriteria
{
    public FlightSearchCriteria(
        AirportCode origin,
        AirportCode destination,
        DateOnly departureDate,
        DateOnly? returnDate,
        PassengerMix passengers,
        CabinClass cabin)
    {
        if (origin == destination)
        {
            throw new ArgumentException("Origin and destination must differ.", nameof(destination));
        }

        if (returnDate < departureDate)
        {
            throw new ArgumentException("The return date cannot be before the departure date.", nameof(returnDate));
        }

        Origin = origin;
        Destination = destination;
        DepartureDate = departureDate;
        ReturnDate = returnDate;
        Passengers = passengers;
        Cabin = cabin;
    }

    public AirportCode Origin { get; }

    public AirportCode Destination { get; }

    public DateOnly DepartureDate { get; }

    public DateOnly? ReturnDate { get; }

    public PassengerMix Passengers { get; }

    public CabinClass Cabin { get; }

    public bool IsRoundTrip => ReturnDate is not null;
}
