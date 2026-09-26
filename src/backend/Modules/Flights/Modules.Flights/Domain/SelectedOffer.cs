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

    /// <summary>
    /// The supplier's fare facts for this offer as last seen (at selection, then at each revalidation): for booking and
    /// receipts. Selections made before these were kept read as not stated.
    /// </summary>
    public FlightFare Fare
    {
        get => StoredFare ?? FlightFare.NotStated;
        private set => StoredFare = value;
    }

    public SelectedOfferStatus Status { get; private set; }

    /// <summary>When the supplier last revalidated the offer (an instant, UTC).</summary>
    public DateTimeOffset? RevalidatedAt { get; private set; }

    /// <summary>
    /// The price confirmed by the supplier AND agreed by the customer: the searched price when revalidation matched
    /// it, or a changed price the customer explicitly accepted. <see cref="TotalPrice"/> keeps what was selected.
    /// </summary>
    public Money? ConfirmedPrice
    {
        get => ToMoney(ConfirmedAmount, ConfirmedCurrency);
        private set => (ConfirmedAmount, ConfirmedCurrency) = FromMoney(value);
    }

    /// <summary>A changed price from the supplier, awaiting the customer's decision (F-01). Never charged unaccepted.</summary>
    public Money? QuotedPrice
    {
        get => ToMoney(QuotedAmount, QuotedCurrency);
        private set => (QuotedAmount, QuotedCurrency) = FromMoney(value);
    }

    /// <summary>Identifies <see cref="QuotedPrice"/>, so the customer accepts exactly the price they were shown.</summary>
    public Guid? PriceQuoteId { get; private set; }

    /// <summary>The last quote the customer accepted; accepting it again is an idempotent replay.</summary>
    public Guid? AcceptedPriceQuoteId { get; private set; }

    /// <summary>When the customer accepted that quote: consent evidence for the order (F-01).</summary>
    public DateTimeOffset? PriceAcceptedAt { get; private set; }

    /// <summary>The price the customer has agreed to so far.</summary>
    public Money AgreedPrice => ConfirmedPrice ?? TotalPrice;

    /// <summary>
    /// Not expired or sold out, so it can still be revalidated. This is NOT "ready to book": booking needs
    /// <see cref="SelectedOfferStatus.Confirmed"/> from a fresh revalidation, never Selected or PriceChanged.
    /// </summary>
    public bool IsAvailable => Status is not (SelectedOfferStatus.Expired or SelectedOfferStatus.SoldOut);

    // Persisted as plain columns: a nullable Money has no single-column form.
    private decimal? ConfirmedAmount { get; set; }

    private CurrencyCode? ConfirmedCurrency { get; set; }

    private decimal? QuotedAmount { get; set; }

    private CurrencyCode? QuotedCurrency { get; set; }

    // Null for rows stored before fare facts were kept.
    private FlightFare? StoredFare { get; set; }

    /// <summary>
    /// Applies the supplier's current offer. The same price as agreed confirms it; any other price (higher or lower,
    /// or another currency) becomes a quote the customer must accept (F-01): nothing is overwritten silently.
    /// </summary>
    public void Revalidate(FlightOffer current, DateTimeOffset now)
    {
        EnsureBookable();
        if (current.Reference.ProviderId != ProviderId)
        {
            throw new InvalidOperationException("A revalidated offer must come from the provider that offered it.");
        }

        // Some suppliers issue a new priced offer at revalidation, and that is the one later operations must use.
        ProviderOfferToken = current.Reference.Value;
        OfferExpiresAt = current.ExpiresAt;
        RevalidatedAt = now;

        // The latest fare facts; after a price change they describe the quoted price the customer is asked to accept.
        Fare = current.ConsistentFare;

        if (current.TotalPrice == AgreedPrice)
        {
            Status = SelectedOfferStatus.Confirmed;
            ConfirmedPrice = AgreedPrice;
            QuotedPrice = null;
            PriceQuoteId = null;
            return;
        }

        // The same changed price keeps its quote id, so a customer accepting it in another tab is not sent round a loop.
        if (Status is not SelectedOfferStatus.PriceChanged || QuotedPrice != current.TotalPrice)
        {
            PriceQuoteId = Guid.NewGuid();
        }

        Status = SelectedOfferStatus.PriceChanged;
        QuotedPrice = current.TotalPrice;
    }

    /// <summary>
    /// The customer accepts the quoted price by its id. Replaying the accepted quote succeeds again; any other quote
    /// is stale (the price changed again, or was never quoted), and an expired offer can no longer be accepted.
    /// </summary>
    public Result<SelectedOffer, AcceptPriceFailure> AcceptPrice(Guid priceQuoteId, DateTimeOffset now)
    {
        if (Status is SelectedOfferStatus.Expired)
        {
            return Result<SelectedOffer, AcceptPriceFailure>.Failure(AcceptPriceFailure.OfferExpired);
        }

        if (Status is SelectedOfferStatus.SoldOut)
        {
            return Result<SelectedOffer, AcceptPriceFailure>.Failure(AcceptPriceFailure.SoldOut);
        }

        if (Status is SelectedOfferStatus.Confirmed && AcceptedPriceQuoteId == priceQuoteId)
        {
            return Result<SelectedOffer, AcceptPriceFailure>.Success(this);
        }

        if (Status is not SelectedOfferStatus.PriceChanged || PriceQuoteId != priceQuoteId)
        {
            return Result<SelectedOffer, AcceptPriceFailure>.Failure(AcceptPriceFailure.StaleQuote);
        }

        if (OfferExpiresAt <= now)
        {
            MarkUnavailable(SelectedOfferStatus.Expired);
            return Result<SelectedOffer, AcceptPriceFailure>.Failure(AcceptPriceFailure.OfferExpired);
        }

        Status = SelectedOfferStatus.Confirmed;
        ConfirmedPrice = QuotedPrice;
        AcceptedPriceQuoteId = priceQuoteId;
        PriceAcceptedAt = now;
        QuotedPrice = null;
        PriceQuoteId = null;
        return Result<SelectedOffer, AcceptPriceFailure>.Success(this);
    }

    /// <summary>F-02 or F-03: the offer can no longer be booked. Terminal; the customer searches again.</summary>
    public void MarkUnavailable(SelectedOfferStatus reason)
    {
        if (reason is not (SelectedOfferStatus.Expired or SelectedOfferStatus.SoldOut))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only Expired or SoldOut make an offer unavailable.");
        }

        if (!IsAvailable)
        {
            return;
        }

        Status = reason;
        QuotedPrice = null;
        PriceQuoteId = null;
    }

    private void EnsureBookable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"A {Status} offer cannot be revalidated.");
        }
    }

    private static Money? ToMoney(decimal? amount, CurrencyCode? currency) =>
        amount is { } a && currency is { } c ? new Money(a, c) : null;

    private static (decimal?, CurrencyCode?) FromMoney(Money? money) => (money?.Amount, money?.Currency);

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
            Fare = offer.ConsistentFare,
            Status = SelectedOfferStatus.Selected,
        };
    }
}

/// <summary>
/// Selected → Confirmed (revalidated at the agreed price) or PriceChanged (a new quote awaits the customer);
/// PriceChanged → Confirmed (quote accepted) or PriceChanged (quoted again); Confirmed → revalidated again before
/// booking; any bookable state → Expired (F-02) or SoldOut (F-03), which are terminal.
/// </summary>
internal enum SelectedOfferStatus
{
    Selected,
    Confirmed,
    PriceChanged,
    Expired,
    SoldOut,
}

internal enum AcceptPriceFailure
{
    /// <summary>The quote is not the current one: the price changed again, or there is nothing to accept.</summary>
    StaleQuote,
    OfferExpired,
    SoldOut,
}
