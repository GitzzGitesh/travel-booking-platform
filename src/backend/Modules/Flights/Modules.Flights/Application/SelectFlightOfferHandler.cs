using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>Persistence port for selected offers; implemented in Infrastructure (architecture rules).</summary>
internal interface ISelectedOfferStore
{
    Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken);

    /// <summary>Adds the snapshot; returns false if this search's offer was already selected (unique constraint).</summary>
    Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken);

    /// <summary>Loads a selection to change it; null if there is none with this id.</summary>
    Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken);

    /// <summary>Saves a loaded selection; false if another request changed it first (optimistic concurrency).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of a selection: the stored snapshot, and whether this request created it.</summary>
internal sealed record SelectedOfferResult(SelectedOffer Offer, bool Created);

/// <summary>Why an offer could not be selected.</summary>
internal enum SelectFlightOfferFailure
{
    /// <summary>F-02: the search expired or was evicted, the offer is unknown, or the offer itself has expired.</summary>
    OfferExpired,
}

/// <summary>
/// Persists a snapshot of the one offer the customer selected, taken from the cached search (Option 2).
/// Idempotent: selecting the same offer of the same search again returns the stored snapshot.
/// </summary>
internal sealed class SelectFlightOfferHandler(FlightSearchCache searchCache, ISelectedOfferStore store, TimeProvider timeProvider)
{
    public async Task<Result<SelectedOfferResult, SelectFlightOfferFailure>> HandleAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken)
    {
        // An offer that was already selected stays selected, even after the search cache has expired.
        if (await store.FindAsync(searchId, offerId, cancellationToken) is { } existing)
        {
            return Result<SelectedOfferResult, SelectFlightOfferFailure>.Success(new SelectedOfferResult(existing, Created: false));
        }

        var search = await searchCache.FindAsync(searchId, cancellationToken);
        var cached = search?.Offers.SingleOrDefault(o => o.OfferId == offerId);
        var now = timeProvider.GetUtcNow();
        if (search is null || cached is null || cached.Offer.ExpiresAt <= now)
        {
            return Result<SelectedOfferResult, SelectFlightOfferFailure>.Failure(SelectFlightOfferFailure.OfferExpired);
        }

        var selected = SelectedOffer.Select(searchId, offerId, search.Criteria, cached.Offer, now);
        if (await store.TryAddAsync(selected, cancellationToken))
        {
            return Result<SelectedOfferResult, SelectFlightOfferFailure>.Success(new SelectedOfferResult(selected, Created: true));
        }

        // A concurrent request selected the same offer first: return its snapshot (exactly one row). This also covers
        // an ambiguous commit retried by the execution strategy, which then reports 200 rather than 201 for our own row.
        var winner = await store.FindAsync(searchId, offerId, cancellationToken)
            ?? throw new InvalidOperationException("The selected offer disappeared after a unique-constraint conflict.");
        return Result<SelectedOfferResult, SelectFlightOfferFailure>.Success(new SelectedOfferResult(winner, Created: false));
    }
}
