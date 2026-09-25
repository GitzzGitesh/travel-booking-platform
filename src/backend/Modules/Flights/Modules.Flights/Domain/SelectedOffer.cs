using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Domain;

/// <summary>
/// A snapshot of the ONE offer a customer selected from a search (Option 2, docs/progress.md). It is supplier-neutral:
/// port types plus the adapter's opaque offer token, which the core stores verbatim and never parses (ADR 0014).
/// It is not a booking: the price and availability are revalidated with the supplier before booking (booking rules).
/// </summary>
internal sealed class SelectedOffer
{
    private SelectedOffer()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The search and the offer within it that the customer selected; unique together (idempotent selection).</summary>
    public Guid SearchId { get; private set; }

    public Guid OfferId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    /// <summary>The adapter-owned opaque offer token (<see cref="ProviderOfferRef.Value"/>).</summary>
    public string ProviderOfferToken { get; private set; } = string.Empty;

    public Money TotalPrice { get; private set; }

    /// <summary>When the supplier's offer expires (an instant, UTC).</summary>
    public DateTimeOffset OfferExpiresAt { get; private set; }

    public DateTimeOffset SelectedAt { get; private set; }

    public int Adults { get; private set; }

    public int Children { get; private set; }

    public int Infants { get; private set; }

    public CabinClass Cabin { get; private set; }

    public IReadOnlyList<FlightSlice> Slices { get; private set; } = [];

    /// <summary>Takes the snapshot. The offer must still be valid at <paramref name="now"/>.</summary>
    public static SelectedOffer Select(Guid searchId, Guid offerId, FlightSearchCriteria criteria, FlightOffer offer, DateTimeOffset now)
    {
        if (offer.ExpiresAt <= now)
        {
            throw new InvalidOperationException("An expired offer cannot be selected.");
        }

        return new SelectedOffer
        {
            Id = Guid.NewGuid(),
            SearchId = searchId,
            OfferId = offerId,
            ProviderId = offer.Reference.ProviderId,
            ProviderOfferToken = offer.Reference.Value,
            TotalPrice = offer.TotalPrice,
            OfferExpiresAt = offer.ExpiresAt,
            SelectedAt = now,
            Adults = criteria.Passengers.Adults,
            Children = criteria.Passengers.Children,
            Infants = criteria.Passengers.Infants,
            Cabin = criteria.Cabin,
            Slices = offer.Slices,
        };
    }
}
