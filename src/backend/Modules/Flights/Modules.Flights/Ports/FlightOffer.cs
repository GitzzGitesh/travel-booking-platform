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
/// (booking rules). <see cref="ExpiresAt"/> is an instant (UTC). <see cref="Fare"/> is what the supplier states about
/// the fare beyond its total; anything it does not state is left empty, never guessed.
/// </summary>
public sealed record FlightOffer(ProviderOfferRef Reference, Money TotalPrice, DateTimeOffset ExpiresAt, IReadOnlyList<FlightSlice> Slices)
{
    public FlightFare Fare { get; init; } = FlightFare.NotStated;

    /// <summary>
    /// The fare facts to trust: a breakdown that does not add up to the total (a supplier's rounding, or a mapping error)
    /// is dropped, never shown or stored; the total stays the price. The core applies this to every offer it receives.
    /// </summary>
    public FlightFare ConsistentFare => Fare.PriceBreakdown is { } breakdown && breakdown.Total != TotalPrice
        ? Fare with { PriceBreakdown = null }
        : Fare;
}

/// <summary>
/// Supplier-neutral fare facts that booking, receipts and the customer need. Every part is optional: suppliers differ in
/// what they return, and an unstated fact is unknown, not "no".
/// </summary>
public sealed record FlightFare
{
    public static readonly FlightFare NotStated = new();

    /// <summary>How the total is made up, per passenger type.</summary>
    public FlightPriceBreakdown? PriceBreakdown { get; init; }

    /// <summary>The airline that issues the ticket (IATA carrier code); it may differ from every flight's marketing carrier.</summary>
    public string? ValidatingCarrier { get; init; }

    /// <summary>Baggage included for each passenger, for the whole itinerary.</summary>
    public BaggageAllowance? Baggage { get; init; }

    public FareConditions Conditions { get; init; } = FareConditions.NotStated;

    /// <summary>
    /// The instant by which the booking must be ticketed, or the supplier cancels it (UTC). Null when the supplier
    /// tickets at booking or does not say.
    /// </summary>
    public DateTimeOffset? TicketingDeadline { get; init; }
}

/// <summary>The fare per passenger type. Taxes and fees are one amount: suppliers itemize them differently.</summary>
public sealed record PassengerFare
{
    public PassengerFare(PassengerType type, int count, Money baseFare, Money taxesAndFees)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (baseFare.Amount < 0 || taxesAndFees.Amount < 0 || baseFare.Currency != taxesAndFees.Currency)
        {
            throw new ArgumentException("A passenger fare is non-negative and in one currency.");
        }

        Type = type;
        Count = count;
        BaseFare = baseFare;
        TaxesAndFees = taxesAndFees;
    }

    public PassengerType Type { get; }

    /// <summary>How many passengers of this type the offer prices.</summary>
    public int Count { get; }

    /// <summary>Per passenger.</summary>
    public Money BaseFare { get; }

    /// <summary>Per passenger.</summary>
    public Money TaxesAndFees { get; }
}

/// <summary>
/// The offer's total split by passenger type: base fare and taxes and fees, per passenger. A type may have several entries
/// (a supplier that prices children by age, or each passenger separately). Its <see cref="Total"/> must be the offer's total.
/// </summary>
public sealed record FlightPriceBreakdown
{
    public FlightPriceBreakdown(IReadOnlyList<PassengerFare> passengers)
    {
        if (passengers.Count == 0 || passengers.Select(p => p.BaseFare.Currency).Distinct().Count() != 1)
        {
            throw new ArgumentException("A price breakdown has at least one entry, all in one currency.", nameof(passengers));
        }

        Passengers = passengers;
    }

    public IReadOnlyList<PassengerFare> Passengers { get; }

    public Money BaseFare => Sum(p => p.BaseFare);

    public Money TaxesAndFees => Sum(p => p.TaxesAndFees);

    public Money Total => BaseFare + TaxesAndFees;

    private Money Sum(Func<PassengerFare, Money> part) =>
        new(Passengers.Sum(p => part(p).Amount * p.Count), Passengers[0].BaseFare.Currency);
}

/// <summary>Baggage included per passenger. A weight limit is per checked bag, when the supplier states one.</summary>
public sealed record BaggageAllowance
{
    public BaggageAllowance(int checkedBags, int cabinBags, int? checkedBagMaxWeightKg = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkedBags);
        ArgumentOutOfRangeException.ThrowIfNegative(cabinBags);
        if (checkedBagMaxWeightKg is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkedBagMaxWeightKg), "A weight limit is positive.");
        }

        CheckedBags = checkedBags;
        CabinBags = cabinBags;
        CheckedBagMaxWeightKg = checkedBagMaxWeightKg;
    }

    public int CheckedBags { get; }

    public int CabinBags { get; }

    public int? CheckedBagMaxWeightKg { get; }
}

/// <summary>Whether the fare can be refunded or changed, and whether at a cost. Penalty amounts come from the fare rules.</summary>
public sealed record FareConditions(FareAllowance Refund, FareAllowance Change)
{
    public static readonly FareConditions NotStated = new(FareAllowance.NotStated, FareAllowance.NotStated);
}

public enum FareAllowance
{
    /// <summary>The supplier did not say: show nothing rather than a guess.</summary>
    NotStated,
    NotAllowed,
    AllowedWithFee,
    Free,
}

/// <summary>One origin-to-destination journey (outbound or return), possibly with connections.</summary>
public sealed record FlightSlice(IReadOnlyList<FlightSegment> Segments);

/// <summary>
/// One flight leg. Departure and arrival are LOCAL times at their own airports, kept as supplied and never converted
/// for storage (ADR 0010). They are in different time zones, so arrival can be "earlier" than departure; the flying time
/// comes from <see cref="Duration"/> or, in the core, from the airports' time zones (airport reference data).
/// </summary>
public sealed record FlightSegment(
    string MarketingCarrier,
    string FlightNumber,
    AirportCode Origin,
    AirportCode Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal)
{
    /// <summary>The airline that flies the aircraft, when it is not the marketing carrier (a codeshare).</summary>
    public string? OperatingCarrier { get; init; }

    /// <summary>The flying time as the supplier states it, if it does.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The supplier's fare basis for this leg (needed for ticketing and fare rules), opaque to the core.</summary>
    public string? FareBasis { get; init; }
}
