using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>An offer as held in a cached search, with the opaque id the customer selects it by.</summary>
internal sealed record CachedFlightOffer(Guid OfferId, FlightOffer Offer);

/// <summary>A search's offers held temporarily for selection (Option 2). Never a booking source of truth (ADR 0011).</summary>
internal sealed record CachedFlightSearch(Guid SearchId, FlightSearchCriteria Criteria, IReadOnlyList<CachedFlightOffer> Offers);

/// <summary>
/// Search results in <see cref="HybridCache"/> (ADR 0011), keyed by the random search id only, so keys never carry PII.
/// An entry expires no later than the earliest offer expiry; eviction or expiry means the customer re-searches (F-02).
/// </summary>
internal sealed partial class FlightSearchCache(HybridCache cache, ILogger<FlightSearchCache> logger, long maximumPayloadBytes = FlightSearchCache.MaximumPayloadBytes)
{
    /// <summary>
    /// Largest cached search. With a distributed L2 (ADR 0011), HybridCache skips larger payloads with only a log entry,
    /// so another instance would silently answer F-02. The size is checked here first, the same way HybridCache
    /// serializes (System.Text.Json), and an oversized search is never cached and is logged as an error.
    /// </summary>
    public const long MaximumPayloadBytes = 1024 * 1024;

    private static readonly HybridCacheEntryOptions _readOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    /// <summary>Stores the search; returns false (and logs) when the cache did not keep it.</summary>
    public async Task<bool> StoreAsync(CachedFlightSearch search, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (search.Offers.Count == 0 || lifetime <= TimeSpan.Zero)
        {
            return false; // Nothing selectable to hold.
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(search).LongLength;
        if (payloadBytes > maximumPayloadBytes)
        {
            LogNotCached(logger, search.SearchId, search.Offers.Count, payloadBytes);
            return false;
        }

        var options = new HybridCacheEntryOptions { Expiration = lifetime, LocalCacheExpiration = lifetime };
        await cache.SetAsync(Key(search.SearchId), search, options, cancellationToken: cancellationToken);
        return true;
    }

    public async Task<CachedFlightSearch?> FindAsync(Guid searchId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync<CachedFlightSearch?>(
            Key(searchId),
            static _ => ValueTask.FromResult<CachedFlightSearch?>(null),
            _readOnly,
            cancellationToken: cancellationToken);

    private static string Key(Guid searchId) => $"flights:search:{searchId:N}";

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight search {SearchId} with {OfferCount} offers was not cached: {PayloadBytes} bytes exceeds the limit; its offers cannot be selected")]
    private static partial void LogNotCached(ILogger logger, Guid searchId, int offerCount, long payloadBytes);
}
