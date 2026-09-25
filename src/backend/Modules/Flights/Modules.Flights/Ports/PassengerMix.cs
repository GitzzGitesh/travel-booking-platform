namespace TravelBooking.Modules.Flights.Ports;

/// <summary>
/// Passenger counts by type (travel-domain skill): at least one adult; each lap infant (INF) travels with an adult;
/// at most <see cref="MaxSeatedPassengers"/> seated passengers per booking, the usual GDS/NDC limit, to revisit
/// per supplier (Q6). Ages are judged at the travel date when passengers are named, not here.
/// </summary>
public sealed record PassengerMix
{
    public const int MaxSeatedPassengers = 9;

    public PassengerMix(int adults, int children = 0, int infants = 0)
    {
        if (!IsValid(adults, children, infants))
        {
            throw new ArgumentOutOfRangeException(nameof(adults), $"Invalid passenger mix: {adults} adults, {children} children, {infants} infants.");
        }

        Adults = adults;
        Children = children;
        Infants = infants;
    }

    public int Adults { get; }

    public int Children { get; }

    public int Infants { get; }

    public int SeatedPassengers => Adults + Children;

    public static bool IsValid(int adults, int children, int infants) =>
        adults >= 1 && children >= 0 && infants >= 0 && infants <= adults && adults + children <= MaxSeatedPassengers;
}
