using System.Collections.Frozen;
using System.Text.Json;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Infrastructure.ReferenceData;

/// <summary>
/// The airport dataset embedded in this assembly (<c>airports.json</c>), loaded and validated once: every code is a valid
/// IATA code, every zone a known IANA zone, and no code appears twice. A bad dataset fails at startup, never mid-request.
/// </summary>
internal sealed class EmbeddedAirportDirectory : IAirportDirectory
{
    private const string _resourceName = "TravelBooking.Modules.Flights.Infrastructure.ReferenceData.airports.json";

    private readonly FrozenDictionary<AirportCode, Airport> _airports;

    public EmbeddedAirportDirectory()
        : this(Load())
    {
    }

    internal EmbeddedAirportDirectory(IEnumerable<Airport> airports)
    {
        var list = airports.ToList();
        if (list.GroupBy(a => a.Code).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"Airport {duplicate.Key} appears more than once in the dataset.");
        }

        _airports = list.ToFrozenDictionary(a => a.Code);
    }

    public int Count => _airports.Count;

    public Airport? Find(AirportCode code) => _airports.GetValueOrDefault(code);

    private static IEnumerable<Airport> Load()
    {
        using var stream = typeof(EmbeddedAirportDirectory).Assembly.GetManifestResourceStream(_resourceName)
            ?? throw new InvalidOperationException($"The airport dataset {_resourceName} is not embedded.");
        var dataset = JsonSerializer.Deserialize<Dataset>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The airport dataset is empty.");
        if (dataset.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported airport dataset version {dataset.Version}.");
        }

        return dataset.Airports.Select(a => new Airport(new AirportCode(a.Code), a.Name, a.City, a.Country, a.TimeZone));
    }

    private sealed record Dataset(int Version, List<Entry> Airports);

    private sealed record Entry(string Code, string Name, string City, string Country, string TimeZone);
}
