using System.Text.Json;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Infrastructure;

/// <summary>
/// The selected itinerary, as stored JSON. Versioned: every version ever written stays readable, because snapshots are
/// kept for booking and receipts. Each leg's operating carrier, stated duration and fare basis are optional additions to
/// version 1: omitted when not stated, and ignored by older readers, so a rollback can still read every row.
/// </summary>
internal static class SliceSnapshotJson
{
    private const int _currentVersion = 1;
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(IReadOnlyList<FlightSlice> slices) =>
        JsonSerializer.Serialize(
            new StoredItinerary(
                _currentVersion,
                slices.Select(slice => new StoredSlice(slice.Segments.Select(segment => new StoredSegment(
                    segment.MarketingCarrier,
                    segment.FlightNumber,
                    segment.Origin.Value,
                    segment.Destination.Value,
                    segment.DepartureLocal,
                    segment.ArrivalLocal,
                    segment.OperatingCarrier,
                    segment.Duration is { } duration ? (int)duration.TotalMinutes : null,
                    segment.FareBasis)).ToList())).ToList()),
            _options);

    public static IReadOnlyList<FlightSlice> Read(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredItinerary>(json, _options)
            ?? throw new InvalidOperationException("An empty itinerary snapshot cannot be read.");
        if (stored.V != _currentVersion)
        {
            throw new InvalidOperationException($"Unsupported itinerary snapshot version {stored.V}.");
        }

        // Rows written before the optional leg facts existed read them as not stated.
        return stored.Slices.Select(slice => new FlightSlice(slice.Segments.Select(segment => new FlightSegment(
            segment.Carrier,
            segment.FlightNumber,
            new AirportCode(segment.Origin),
            new AirportCode(segment.Destination),
            DateTime.SpecifyKind(segment.DepartureLocal, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(segment.ArrivalLocal, DateTimeKind.Unspecified))
        {
            OperatingCarrier = segment.OperatingCarrier,
            Duration = segment.DurationMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            FareBasis = segment.FareBasis,
        }).ToList())).ToList();
    }

    // Local times at their airports (ADR 0010), stored without an offset.
    private sealed record StoredItinerary(int V, List<StoredSlice> Slices);

    private sealed record StoredSlice(List<StoredSegment> Segments);

    private sealed record StoredSegment(
        string Carrier,
        string FlightNumber,
        string Origin,
        string Destination,
        DateTime DepartureLocal,
        DateTime ArrivalLocal,
        string? OperatingCarrier = null,
        int? DurationMinutes = null,
        string? FareBasis = null);
}

/// <summary>
/// The fare facts of the selected offer (price breakdown, validating carrier, baggage, conditions, ticketing deadline) as
/// stored JSON, versioned like the itinerary. Money keeps its exact decimal amount and currency.
/// </summary>
internal static class FareSnapshotJson
{
    private const int _currentVersion = 1;
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public static string Write(FlightFare fare) =>
        JsonSerializer.Serialize(
            new StoredFare(
                _currentVersion,
                fare.PriceBreakdown?.Passengers.Select(p => new StoredPassengerFare(
                    p.Type.ToString(), p.Count, p.BaseFare.Amount, p.TaxesAndFees.Amount, p.BaseFare.Currency.Value)).ToList(),
                fare.ValidatingCarrier,
                fare.Baggage is { } baggage ? new StoredBaggage(baggage.CheckedBags, baggage.CabinBags, baggage.CheckedBagMaxWeightKg) : null,
                fare.Conditions.Refund.ToString(),
                fare.Conditions.Change.ToString(),
                fare.TicketingDeadline),
            _options);

    public static FlightFare Read(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredFare>(json, _options)
            ?? throw new InvalidOperationException("An empty fare snapshot cannot be read.");
        if (stored.V != _currentVersion)
        {
            throw new InvalidOperationException($"Unsupported fare snapshot version {stored.V}.");
        }

        return new FlightFare
        {
            PriceBreakdown = stored.Passengers is { Count: > 0 } passengers
                ? new FlightPriceBreakdown([.. passengers.Select(p => new PassengerFare(
                    Enum.Parse<PassengerType>(p.Type),
                    p.Count,
                    new Money(p.BaseFare, new CurrencyCode(p.Currency)),
                    new Money(p.TaxesAndFees, new CurrencyCode(p.Currency))))])
                : null,
            ValidatingCarrier = stored.ValidatingCarrier,
            Baggage = stored.Baggage is { } baggage ? new BaggageAllowance(baggage.CheckedBags, baggage.CabinBags, baggage.CheckedBagMaxWeightKg) : null,
            Conditions = new FareConditions(Enum.Parse<FareAllowance>(stored.Refund), Enum.Parse<FareAllowance>(stored.Change)),
            TicketingDeadline = stored.TicketingDeadline,
        };
    }

    private sealed record StoredFare(
        int V,
        List<StoredPassengerFare>? Passengers,
        string? ValidatingCarrier,
        StoredBaggage? Baggage,
        string Refund,
        string Change,
        DateTimeOffset? TicketingDeadline);

    private sealed record StoredPassengerFare(string Type, int Count, decimal BaseFare, decimal TaxesAndFees, string Currency);

    private sealed record StoredBaggage(int CheckedBags, int CabinBags, int? CheckedBagMaxWeightKg);
}
