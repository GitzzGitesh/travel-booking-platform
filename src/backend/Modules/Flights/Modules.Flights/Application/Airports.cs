using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>
/// Reference data for one airport: what the customer sees, and the IANA time zone its local times are in (ADR 0010:
/// flight times stay local, with their zone). Core reference data, not part of the provider port (ADR 0014): adapters
/// map the supplier's local times as given. A small dataset for now; an authoritative source can replace it behind
/// <see cref="IAirportDirectory"/>.
/// </summary>
internal sealed record Airport
{
    public Airport(AirportCode code, string name, string cityName, string countryCode, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cityName);
        if (countryCode is not { Length: 2 } || !countryCode.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{countryCode}' is not an ISO 3166-1 alpha-2 country code.", nameof(countryCode));
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var timeZone) || !timeZone.HasIanaId)
        {
            throw new ArgumentException($"'{timeZoneId}' is not a known IANA time zone.", nameof(timeZoneId));
        }

        Code = code;
        Name = name;
        CityName = cityName;
        CountryCode = countryCode;
        TimeZoneId = timeZoneId;
        TimeZone = timeZone;
    }

    public AirportCode Code { get; }

    public string Name { get; }

    public string CityName { get; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string CountryCode { get; }

    /// <summary>The IANA identifier, such as Europe/London.</summary>
    public string TimeZoneId { get; }

    internal TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// The instant a local time at this airport denotes. Null for a time that does not exist there (skipped by a
    /// daylight-saving change); an ambiguous time (repeated) is read as standard time.
    /// </summary>
    public DateTimeOffset? ToInstant(DateTime localTime)
    {
        var local = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local))
        {
            return null;
        }

        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    /// <summary>The local time at this airport for an instant.</summary>
    public DateTime ToLocal(DateTimeOffset instant) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime, DateTimeKind.Unspecified);
}

/// <summary>Looks airports up by IATA code. An airport outside the dataset is not an error: it is shown by its code.</summary>
internal interface IAirportDirectory
{
    Airport? Find(AirportCode code);

    /// <summary>
    /// The flying time of a leg: as the supplier states it, otherwise from both airports' time zones. Null when neither
    /// is available.
    /// </summary>
    TimeSpan? DurationOf(FlightSegment segment) =>
        segment.Duration
        ?? (Find(segment.Origin)?.ToInstant(segment.DepartureLocal) is { } departs && Find(segment.Destination)?.ToInstant(segment.ArrivalLocal) is { } arrives
            ? arrives - departs
            : null);
}
