using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Domain;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.Application;

/// <summary>The configured hotel providers by id: an offer is always revalidated by the provider that made it.</summary>
internal sealed class HotelProviders(IEnumerable<IHotelProvider> providers)
{
    private readonly IReadOnlyList<IHotelProvider> _providers = [.. providers];

    /// <summary>The provider searches go to; none configured means hotel search is unavailable here.</summary>
    public IHotelProvider? ForSearch => _providers.Count == 1 ? _providers[0] : null;

    public IHotelProvider? Find(string providerId) => _providers.SingleOrDefault(p => p.Id == providerId);
}

/// <summary>An offer as held in a cached search, with the opaque id the customer selects it by.</summary>
internal sealed record CachedHotelOffer(Guid OfferId, HotelOffer Offer);

internal sealed record CachedHotelSearch(Guid SearchId, HotelSearchCriteria Criteria, IReadOnlyList<CachedHotelOffer> Offers);

/// <summary>
/// Search results in HybridCache (ADR 0011), keyed by the random search id only (no PII in keys), never outliving the
/// earliest offer expiry. Never a booking source of truth: eviction means the customer searches again (F-02).
/// </summary>
internal sealed partial class HotelSearchCache(HybridCache cache, ILogger<HotelSearchCache> logger)
{
    public const long MaximumPayloadBytes = 1024 * 1024;

    private static readonly HybridCacheEntryOptions _readOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    public async Task StoreAsync(CachedHotelSearch search, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (search.Offers.Count == 0 || lifetime <= TimeSpan.Zero)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(search).LongLength;
        if (bytes > MaximumPayloadBytes)
        {
            LogNotCached(logger, search.SearchId, search.Offers.Count, bytes);
            return;
        }

        await cache.SetAsync(Key(search.SearchId), search, new HybridCacheEntryOptions { Expiration = lifetime, LocalCacheExpiration = lifetime },
            cancellationToken: cancellationToken);
    }

    public async Task<CachedHotelSearch?> FindAsync(Guid searchId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync<CachedHotelSearch?>(Key(searchId), static _ => ValueTask.FromResult<CachedHotelSearch?>(null), _readOnly,
            cancellationToken: cancellationToken);

    private static string Key(Guid searchId) => $"hotels:search:{searchId:N}";

    [LoggerMessage(Level = LogLevel.Error, Message = "Hotel search {SearchId} with {OfferCount} offers was not cached: {PayloadBytes} bytes exceeds the limit")]
    private static partial void LogNotCached(ILogger logger, Guid searchId, int offerCount, long payloadBytes);
}

internal abstract record SearchHotelsFailure
{
    private SearchHotelsFailure()
    {
    }

    internal sealed record InvalidDates(string Field, string Message) : SearchHotelsFailure;

    internal sealed record ProviderFailed(ProviderError Error) : SearchHotelsFailure;
}

/// <summary>Runs a hotel search and holds its offers under a new random search id, for selection (ADR 0030).</summary>
internal sealed partial class SearchHotelsHandler(HotelProviders providers, HotelSearchCache cache, TimeProvider timeProvider, ILogger<SearchHotelsHandler> logger)
{
    /// <summary>How far ahead stays can be searched (a supplier constraint, to revisit per supplier, Q6).</summary>
    internal const int SalesHorizonDays = 365;

    public async Task<Result<CachedHotelSearch, SearchHotelsFailure>> HandleAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken)
    {
        // Local dates at the property, whose time zone is not known before the search: "yesterday in UTC" is the
        // strictest lower bound that never refuses a valid local check-in date (as for flights).
        var todayUtc = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (criteria.CheckIn < todayUtc.AddDays(-1))
        {
            return Failure(new SearchHotelsFailure.InvalidDates(nameof(criteria.CheckIn), "The check-in date is in the past."));
        }

        if (criteria.CheckIn > todayUtc.AddDays(SalesHorizonDays))
        {
            return Failure(new SearchHotelsFailure.InvalidDates(nameof(criteria.CheckIn), $"Stays can be searched at most {SalesHorizonDays} days ahead."));
        }

        if (criteria.Nights is < 1 or > HotelSearchCriteria.MaxNights)
        {
            return Failure(new SearchHotelsFailure.InvalidDates(nameof(criteria.CheckOut),
                $"The check-out date must be 1 to {HotelSearchCriteria.MaxNights} nights after check-in."));
        }

        if (providers.ForSearch is not { } provider)
        {
            return Failure(new SearchHotelsFailure.ProviderFailed(new ProviderError(ProviderErrorKind.Unavailable, "No hotel provider is configured.")));
        }

        var result = await provider.SearchAsync(criteria, cancellationToken);
        if (!result.IsSuccess)
        {
            LogProviderFailure(logger, provider.Id, result.Error.Kind);
            return Failure(new SearchHotelsFailure.ProviderFailed(result.Error));
        }

        var now = timeProvider.GetUtcNow();
        // Only offers we can sell and show honestly: from this provider, unexpired, priced, with consistent cancellation terms.
        var offers = result.Value.Offers
            .Where(o => o.Reference.ProviderId == provider.Id && o.ExpiresAt > now && o.IsSellable())
            .Select(o => new CachedHotelOffer(Guid.NewGuid(), o))
            .ToList();
        if (offers.Count < result.Value.Offers.Count)
        {
            LogOffersDropped(logger, provider.Id, result.Value.Offers.Count - offers.Count);
        }

        var search = new CachedHotelSearch(Guid.NewGuid(), criteria, offers);
        await cache.StoreAsync(search, offers.Count == 0 ? TimeSpan.Zero : offers.Min(o => o.Offer.ExpiresAt) - now, cancellationToken);
        return Result<CachedHotelSearch, SearchHotelsFailure>.Success(search);
    }

    private static Result<CachedHotelSearch, SearchHotelsFailure> Failure(SearchHotelsFailure failure) =>
        Result<CachedHotelSearch, SearchHotelsFailure>.Failure(failure);

    // Provider id and error category only: never the search criteria, which describe a person's travel.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotel search failed at provider {ProviderId}: {ErrorKind}")]
    private static partial void LogProviderFailure(ILogger logger, string providerId, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotel provider {ProviderId} returned {Count} offers that cannot be sold as stated; they were dropped")]
    private static partial void LogOffersDropped(ILogger logger, string providerId, int count);
}

/// <summary>Persistence port for hotel selections; implemented in Infrastructure.</summary>
internal interface IHotelSelectionStore
{
    Task<HotelSelection?> FindAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken);

    /// <summary>False if this caller already selected this search's offer (unique constraint).</summary>
    Task<bool> TryAddAsync(HotelSelection selection, CancellationToken cancellationToken);

    Task<HotelSelection?> FindForUpdateAsync(Guid selectionId, CancellationToken cancellationToken);

    /// <summary>A read (not tracked).</summary>
    Task<HotelSelection?> FindByIdAsync(Guid selectionId, CancellationToken cancellationToken);

    /// <summary>False if another request changed it first (optimistic concurrency).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

internal sealed record HotelSelectionResult(HotelSelection Selection, bool Created);

/// <summary>
/// Persists a snapshot of the one offer the customer selected, from the cached search. Idempotent per caller: selecting
/// the same offer again returns the stored snapshot, even after the search cache expired.
/// </summary>
internal sealed class SelectHotelOfferHandler(HotelSearchCache cache, IHotelSelectionStore store, TimeProvider timeProvider)
{
    public async Task<HotelSelectionResult?> HandleAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(searchId, offerId, customerId, cancellationToken) is { } existing)
        {
            return new HotelSelectionResult(existing, Created: false);
        }

        var search = await cache.FindAsync(searchId, cancellationToken);
        var cached = search?.Offers.SingleOrDefault(o => o.OfferId == offerId);
        var now = timeProvider.GetUtcNow();
        if (search is null || cached is null || cached.Offer.ExpiresAt <= now)
        {
            return null; // F-02: search again
        }

        var selection = HotelSelection.Select(searchId, offerId, search.Criteria, cached.Offer, now, customerId);
        if (await store.TryAddAsync(selection, cancellationToken))
        {
            return new HotelSelectionResult(selection, Created: true);
        }

        // A concurrent request selected the same offer first: its snapshot, exactly one row.
        return new HotelSelectionResult(
            await store.FindAsync(searchId, offerId, customerId, cancellationToken)
                ?? throw new InvalidOperationException("The selection disappeared after a unique-constraint conflict."),
            Created: false);
    }
}

internal abstract record HotelSelectionFailure
{
    private HotelSelectionFailure()
    {
    }

    internal sealed record NotFound : HotelSelectionFailure;

    internal sealed record PriceChanged(HotelSelection Selection) : HotelSelectionFailure;

    internal sealed record OfferExpired : HotelSelectionFailure;

    internal sealed record SoldOut : HotelSelectionFailure;

    internal sealed record StaleQuote : HotelSelectionFailure;

    internal sealed record Conflict : HotelSelectionFailure;

    /// <summary>The stay is booked (or its booking was sent): the selection is frozen and never revalidated or repriced again.</summary>
    internal sealed record Booked : HotelSelectionFailure;

    internal sealed record ProviderFailed(ProviderError Error) : HotelSelectionFailure;
}

/// <summary>
/// Revalidates the selection with its own provider before any booking step: Confirmed at the agreed price, or a quote
/// the customer must accept (F-01), or expired (F-02, also decided locally past its expiry) or sold out (F-03).
/// </summary>
internal sealed class RevalidateHotelSelectionHandler(IHotelSelectionStore store, HotelProviders providers, TimeProvider timeProvider)
{
    public async Task<Result<HotelSelection, HotelSelectionFailure>> HandleAsync(Guid selectionId, string? customerId, CancellationToken cancellationToken)
    {
        var selection = await store.FindForUpdateAsync(selectionId, cancellationToken);
        if (selection is null || !selection.IsVisibleTo(customerId))
        {
            return Failure(new HotelSelectionFailure.NotFound());
        }

        if (Unavailable(selection) is { } unavailable)
        {
            return Failure(unavailable);
        }

        var now = timeProvider.GetUtcNow();
        if (selection.OfferExpiresAt <= now)
        {
            return await MarkUnavailable(selection, HotelSelectionStatus.Expired, cancellationToken);
        }

        if (providers.Find(selection.ProviderId) is not { } provider)
        {
            // Not terminal: configuration can change back. Never sent to another provider.
            return Failure(new HotelSelectionFailure.ProviderFailed(new ProviderError(ProviderErrorKind.Unavailable, "The offer's provider is not configured.")));
        }

        var current = await provider.RevalidateAsync(new HotelOfferRef(selection.ProviderId, selection.ProviderOfferToken), cancellationToken);
        if (!current.IsSuccess)
        {
            return current.Error.Kind switch
            {
                ProviderErrorKind.OfferExpired => await MarkUnavailable(selection, HotelSelectionStatus.Expired, cancellationToken),
                ProviderErrorKind.SoldOut => await MarkUnavailable(selection, HotelSelectionStatus.SoldOut, cancellationToken),
                _ => Failure(new HotelSelectionFailure.ProviderFailed(current.Error)),
            };
        }

        if (current.Value.ExpiresAt <= now)
        {
            return await MarkUnavailable(selection, HotelSelectionStatus.Expired, cancellationToken);
        }

        if (!current.Value.IsSellable())
        {
            return Failure(new HotelSelectionFailure.ProviderFailed(new ProviderError(ProviderErrorKind.Unknown, "The revalidated offer cannot be sold as stated.")));
        }

        // Another property is not the stay the customer chose: the selection ends (as sold out), and they search again.
        if (current.Value.Property.Id != selection.PropertyId)
        {
            return await MarkUnavailable(selection, HotelSelectionStatus.SoldOut, cancellationToken);
        }

        selection.Revalidate(current.Value, now);
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Failure(new HotelSelectionFailure.Conflict());
        }

        return selection.Status is HotelSelectionStatus.PriceChanged
            ? Failure(new HotelSelectionFailure.PriceChanged(selection))
            : Result<HotelSelection, HotelSelectionFailure>.Success(selection);
    }

    internal static HotelSelectionFailure? Unavailable(HotelSelection selection) => selection.Status switch
    {
        HotelSelectionStatus.Expired => new HotelSelectionFailure.OfferExpired(),
        HotelSelectionStatus.SoldOut => new HotelSelectionFailure.SoldOut(),
        HotelSelectionStatus.Booking or HotelSelectionStatus.Booked => new HotelSelectionFailure.Booked(),
        _ => null,
    };

    private async Task<Result<HotelSelection, HotelSelectionFailure>> MarkUnavailable(
        HotelSelection selection, HotelSelectionStatus reason, CancellationToken cancellationToken)
    {
        selection.MarkUnavailable(reason);
        return await store.TrySaveAsync(cancellationToken) ? Failure(Unavailable(selection)!) : Failure(new HotelSelectionFailure.Conflict());
    }

    private static Result<HotelSelection, HotelSelectionFailure> Failure(HotelSelectionFailure failure) =>
        Result<HotelSelection, HotelSelectionFailure>.Failure(failure);
}

/// <summary>The customer accepts a changed price by the quote id they were shown (F-01). The price never comes from the client.</summary>
internal sealed class AcceptHotelPriceHandler(IHotelSelectionStore store, TimeProvider timeProvider)
{
    public async Task<Result<HotelSelection, HotelSelectionFailure>> HandleAsync(Guid selectionId, Guid priceQuoteId, string? customerId, CancellationToken cancellationToken)
    {
        var selection = await store.FindForUpdateAsync(selectionId, cancellationToken);
        if (selection is null || !selection.IsVisibleTo(customerId))
        {
            return Result<HotelSelection, HotelSelectionFailure>.Failure(new HotelSelectionFailure.NotFound());
        }

        var accepted = selection.AcceptPrice(priceQuoteId, timeProvider.GetUtcNow());
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Result<HotelSelection, HotelSelectionFailure>.Failure(new HotelSelectionFailure.Conflict());
        }

        return accepted.IsSuccess
            ? Result<HotelSelection, HotelSelectionFailure>.Success(selection)
            : Result<HotelSelection, HotelSelectionFailure>.Failure(accepted.Error switch
            {
                HotelPriceAcceptanceFailure.OfferExpired => new HotelSelectionFailure.OfferExpired(),
                HotelPriceAcceptanceFailure.SoldOut => new HotelSelectionFailure.SoldOut(),
                HotelPriceAcceptanceFailure.Booked => new HotelSelectionFailure.Booked(),
                _ => new HotelSelectionFailure.StaleQuote(),
            });
    }
}
