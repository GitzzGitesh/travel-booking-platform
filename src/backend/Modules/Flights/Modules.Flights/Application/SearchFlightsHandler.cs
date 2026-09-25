using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>Why a search did not produce offers. Each case maps to one HTTP problem.</summary>
internal abstract record SearchFlightsFailure
{
    internal sealed record InvalidDates(string Field, string Message) : SearchFlightsFailure;

    internal sealed record ProviderFailed(ProviderError Error) : SearchFlightsFailure;
}

/// <summary>Runs a flight search against the composed <see cref="IFlightProvider"/>.</summary>
internal sealed partial class SearchFlightsHandler(IFlightProvider provider, TimeProvider timeProvider, ILogger<SearchFlightsHandler> logger)
{
    /// <summary>
    /// How far ahead flights can be searched: the longest airline sales horizon (about 361 days). A supplier
    /// constraint, to revisit per supplier (Q6), like <see cref="PassengerMix.MaxSeatedPassengers"/>.
    /// </summary>
    internal const int SalesHorizonDays = 361;

    public async Task<Result<FlightSearchResult, SearchFlightsFailure>> HandleAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
    {
        if (CheckDates(criteria) is { } invalidDates)
        {
            return Result<FlightSearchResult, SearchFlightsFailure>.Failure(invalidDates);
        }

        var result = await provider.SearchAsync(criteria, cancellationToken);
        if (result.IsSuccess)
        {
            return Result<FlightSearchResult, SearchFlightsFailure>.Success(result.Value);
        }

        if (result.Error.Kind == ProviderErrorKind.AuthFailure)
        {
            // Our credentials or configuration are broken: an operator must act (provider-integration.md: alert immediately).
            LogProviderAuthFailure(logger, provider.Id);
        }
        else
        {
            LogProviderFailure(logger, provider.Id, result.Error.Kind);
        }

        return Result<FlightSearchResult, SearchFlightsFailure>.Failure(new SearchFlightsFailure.ProviderFailed(result.Error));
    }

    // Dates are local to their airports, and airport time zones are not modelled yet. The earliest local date anywhere
    // is one day behind UTC, so "yesterday in UTC" is the strictest lower bound that never rejects a valid date.
    private SearchFlightsFailure.InvalidDates? CheckDates(FlightSearchCriteria criteria)
    {
        var todayUtc = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var latest = todayUtc.AddDays(SalesHorizonDays);

        if (criteria.DepartureDate < todayUtc.AddDays(-1))
        {
            return new(nameof(criteria.DepartureDate), "The departure date is in the past.");
        }

        if (criteria.DepartureDate > latest)
        {
            return new(nameof(criteria.DepartureDate), $"Flights can be searched at most {SalesHorizonDays} days ahead.");
        }

        if (criteria.ReturnDate > latest)
        {
            return new(nameof(criteria.ReturnDate), $"Flights can be searched at most {SalesHorizonDays} days ahead.");
        }

        return null;
    }

    // Provider id and taxonomy category only: never search criteria, which describe a person's travel.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight search failed at provider {ProviderId}: {ErrorKind}")]
    private static partial void LogProviderFailure(ILogger logger, string providerId, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight provider {ProviderId} rejected our credentials (AuthFailure)")]
    private static partial void LogProviderAuthFailure(ILogger logger, string providerId);
}
