using System.Text.Json;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Infrastructure;

/// <summary>
/// The stored JSON contract for a selected offer's itinerary. It is owned by Infrastructure and versioned, and it maps
/// to and from the port types by hand, so a change to the (not yet frozen) port never silently breaks stored rows.
/// A new shape means a new version plus a reader for the old one; unknown versions fail loudly.
/// </summary>
internal static class SliceSnapshotJson
{
    private const int _currentVersion = 1;
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

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
                    segment.ArrivalLocal)).ToList())).ToList()),
            _options);

    public static IReadOnlyList<FlightSlice> Read(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredItinerary>(json, _options)
            ?? throw new InvalidOperationException("An empty itinerary snapshot cannot be read.");
        if (stored.V != _currentVersion)
        {
            throw new InvalidOperationException($"Unsupported itinerary snapshot version {stored.V}.");
        }

        return stored.Slices.Select(slice => new FlightSlice(slice.Segments.Select(segment => new FlightSegment(
            segment.Carrier,
            segment.FlightNumber,
            new AirportCode(segment.Origin),
            new AirportCode(segment.Destination),
            DateTime.SpecifyKind(segment.DepartureLocal, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(segment.ArrivalLocal, DateTimeKind.Unspecified))).ToList())).ToList();
    }

    // Version 1. Local times at their airports (ADR 0010), stored without an offset.
    private sealed record StoredItinerary(int V, List<StoredSlice> Slices);

    private sealed record StoredSlice(List<StoredSegment> Segments);

    private sealed record StoredSegment(string Carrier, string FlightNumber, string Origin, string Destination, DateTime DepartureLocal, DateTime ArrivalLocal);
}
