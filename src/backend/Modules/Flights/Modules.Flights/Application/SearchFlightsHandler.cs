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

/// <summary>
/// Runs a flight search against the search provider (<see cref="FlightProviders.ForSearch"/>), then holds the offers in the search cache
/// under a new random search id so the customer can select one (Option 2).
/// </summary>
internal sealed partial class SearchFlightsHandler(
    FlightProviders providers,
    FlightSearchCache searchCache,
    TimeProvider timeProvider,
    ILogger<SearchFlightsHandler> logger)
{
    /// <summary>
    /// How far ahead flights can be searched: the longest airline sales horizon (about 361 days). A supplier
    /// constraint, to revisit per supplier (Q6), like <see cref="PassengerMix.MaxSeatedPassengers"/>.
    /// </summary>
    internal const int SalesHorizonDays = 361;

    public async Task<Result<CachedFlightSearch, SearchFlightsFailure>> HandleAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
    {
        if (CheckDates(criteria) is { } invalidDates)
        {
            return Result<CachedFlightSearch, SearchFlightsFailure>.Failure(invalidDates);
        }

        var provider = providers.ForSearch;
        var result = await provider.SearchAsync(criteria, cancellationToken);
        if (result.IsSuccess)
        {
            // Random (not time-ordered) ids: they identify a search and an offer, and must not be guessable.
            var search = new CachedFlightSearch(
                Guid.NewGuid(),
                criteria,
                result.Value.Offers.Select(offer => new CachedFlightOffer(Guid.NewGuid(), Checked(offer, provider.Id))).ToList());
            await searchCache.StoreAsync(search, CacheLifetime(search), cancellationToken);
            return Result<CachedFlightSearch, SearchFlightsFailure>.Success(search);
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

        return Result<CachedFlightSearch, SearchFlightsFailure>.Failure(new SearchFlightsFailure.ProviderFailed(result.Error));
    }

    // The cache entry never outlives the earliest-expiring offer in it (ADR 0011: offer expiry bounds the TTL).
    private TimeSpan CacheLifetime(CachedFlightSearch search) =>
        search.Offers.Count == 0
            ? TimeSpan.Zero
            : search.Offers.Min(o => o.Offer.ExpiresAt) - timeProvider.GetUtcNow();

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

    // A breakdown that does not add up is dropped, never shown (the total stays the price), and logged for the adapter's owner.
    private FlightOffer Checked(FlightOffer offer, string providerId)
    {
        var fare = offer.ConsistentFare;
        if (ReferenceEquals(fare, offer.Fare))
        {
            return offer;
        }

        LogInconsistentBreakdown(logger, providerId);
        return offer with { Fare = fare };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight provider {ProviderId} returned a price breakdown that does not add up to the offer total; it was dropped")]
    private static partial void LogInconsistentBreakdown(ILogger logger, string providerId);

    // Provider id and taxonomy category only: never search criteria, which describe a person's travel.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight search failed at provider {ProviderId}: {ErrorKind}")]
    private static partial void LogProviderFailure(ILogger logger, string providerId, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight provider {ProviderId} rejected our credentials (AuthFailure)")]
    private static partial void LogProviderAuthFailure(ILogger logger, string providerId);
}
