using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>Why a selected offer could not be revalidated, or its new price accepted.</summary>
internal abstract record SelectedOfferFailure
{
    private SelectedOfferFailure()
    {
    }

    internal sealed record NotFound : SelectedOfferFailure;

    /// <summary>F-01: the supplier's price differs from the agreed one; the offer holds the quote to show and accept.</summary>
    internal sealed record PriceChanged(SelectedOffer Offer) : SelectedOfferFailure;

    /// <summary>F-02: the offer expired. The customer searches again.</summary>
    internal sealed record OfferExpired : SelectedOfferFailure;

    /// <summary>F-03: the offer sold out. The customer searches again.</summary>
    internal sealed record SoldOut : SelectedOfferFailure;

    /// <summary>The quote being accepted is not the current one (the price changed again, or nothing was quoted).</summary>
    internal sealed record StaleQuote : SelectedOfferFailure;

    /// <summary>Another request changed this selection at the same time; retrying is safe.</summary>
    internal sealed record Conflict : SelectedOfferFailure;

    /// <summary>The supplier could not answer (unavailable, throttled, or an unexpected error); nothing changed.</summary>
    internal sealed record ProviderFailed(ProviderError Error) : SelectedOfferFailure;
}

/// <summary>
/// Revalidates the customer's selected offer with the supplier before any booking step (booking rules: never trust a
/// stored or client price). The stored selection is the source: its opaque provider token is sent back verbatim.
/// Outcomes: Confirmed (same price), or a failure: PriceChanged (a saved quote the customer must accept, F-01), expired
/// (F-02, also decided locally once the offer's expiry has passed, without calling the supplier) or sold out (F-03).
/// Revalidation is a read at the supplier, so repeating it is safe.
/// </summary>
internal sealed class RevalidateSelectedOfferHandler(ISelectedOfferStore store, IFlightProvider provider, TimeProvider timeProvider)
{
    public async Task<Result<SelectedOffer, SelectedOfferFailure>> HandleAsync(Guid selectedOfferId, CancellationToken cancellationToken)
    {
        var offer = await store.FindForUpdateAsync(selectedOfferId, cancellationToken);
        if (offer is null)
        {
            return Failure(new SelectedOfferFailure.NotFound());
        }

        if (Unavailable(offer) is { } unavailable)
        {
            return Failure(unavailable);
        }

        var now = timeProvider.GetUtcNow();
        if (offer.OfferExpiresAt <= now)
        {
            return await MarkUnavailable(offer, SelectedOfferStatus.Expired, cancellationToken);
        }

        if (offer.ProviderId != provider.Id)
        {
            // Its provider is not configured here (for now): not terminal, as configuration can change back.
            return Failure(new SelectedOfferFailure.ProviderFailed(new ProviderError(ProviderErrorKind.Unavailable, "The offer's provider is not configured.")));
        }

        var revalidated = await provider.RevalidateAsync(new ProviderOfferRef(offer.ProviderId, offer.ProviderOfferToken), cancellationToken);
        if (!revalidated.IsSuccess)
        {
            return revalidated.Error.Kind switch
            {
                ProviderErrorKind.OfferExpired => await MarkUnavailable(offer, SelectedOfferStatus.Expired, cancellationToken),
                ProviderErrorKind.SoldOut => await MarkUnavailable(offer, SelectedOfferStatus.SoldOut, cancellationToken),
                _ => Failure(new SelectedOfferFailure.ProviderFailed(revalidated.Error)),
            };
        }

        if (revalidated.Value.ExpiresAt <= now)
        {
            return await MarkUnavailable(offer, SelectedOfferStatus.Expired, cancellationToken);
        }

        offer.Revalidate(revalidated.Value, now);
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Failure(new SelectedOfferFailure.Conflict());
        }

        // The quote is saved, but the offer cannot proceed until the customer accepts it (F-01).
        return offer.Status is SelectedOfferStatus.PriceChanged
            ? Failure(new SelectedOfferFailure.PriceChanged(offer))
            : Result<SelectedOffer, SelectedOfferFailure>.Success(offer);
    }

    internal static SelectedOfferFailure? Unavailable(SelectedOffer offer) => offer.Status switch
    {
        SelectedOfferStatus.Expired => new SelectedOfferFailure.OfferExpired(),
        SelectedOfferStatus.SoldOut => new SelectedOfferFailure.SoldOut(),
        _ => null,
    };

    private async Task<Result<SelectedOffer, SelectedOfferFailure>> MarkUnavailable(
        SelectedOffer offer, SelectedOfferStatus reason, CancellationToken cancellationToken)
    {
        offer.MarkUnavailable(reason);
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Failure(new SelectedOfferFailure.Conflict());
        }

        return Failure(Unavailable(offer)!);
    }

    private static Result<SelectedOffer, SelectedOfferFailure> Failure(SelectedOfferFailure failure) =>
        Result<SelectedOffer, SelectedOfferFailure>.Failure(failure);
}

/// <summary>
/// The customer accepts a changed price (F-01) by the quote id they were shown. Idempotent: accepting the same quote
/// again returns the confirmed selection. The price itself never comes from the client.
/// </summary>
internal sealed class AcceptSelectedOfferPriceHandler(ISelectedOfferStore store, TimeProvider timeProvider)
{
    public async Task<Result<SelectedOffer, SelectedOfferFailure>> HandleAsync(Guid selectedOfferId, Guid priceQuoteId, CancellationToken cancellationToken)
    {
        var offer = await store.FindForUpdateAsync(selectedOfferId, cancellationToken);
        if (offer is null)
        {
            return Result<SelectedOffer, SelectedOfferFailure>.Failure(new SelectedOfferFailure.NotFound());
        }

        var accepted = offer.AcceptPrice(priceQuoteId, timeProvider.GetUtcNow());

        // Acceptance changes the row, and so may an expiry found while accepting: both are saved.
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Result<SelectedOffer, SelectedOfferFailure>.Failure(new SelectedOfferFailure.Conflict());
        }

        return accepted.IsSuccess
            ? Result<SelectedOffer, SelectedOfferFailure>.Success(offer)
            : Result<SelectedOffer, SelectedOfferFailure>.Failure(accepted.Error switch
            {
                AcceptPriceFailure.OfferExpired => new SelectedOfferFailure.OfferExpired(),
                AcceptPriceFailure.SoldOut => new SelectedOfferFailure.SoldOut(),
                _ => new SelectedOfferFailure.StaleQuote(),
            });
    }
}
