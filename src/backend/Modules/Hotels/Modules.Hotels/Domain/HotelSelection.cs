using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.Domain;

/// <summary>
/// Selected → Confirmed (revalidated at the agreed price) or PriceChanged (a quote awaits the customer); PriceChanged →
/// Confirmed (quote accepted) or PriceChanged (quoted again); any bookable state → Expired (F-02) or SoldOut (F-03),
/// which are terminal; Confirmed → Booking when its booking is sent (frozen from then on: never revalidated or repriced,
/// so what is booked is what the customer agreed to) → Booked once the supplier holds it. The same rules as a selected
/// flight offer (ADR 0030).
/// </summary>
internal enum HotelSelectionStatus
{
    Selected,
    Confirmed,
    PriceChanged,
    Expired,
    SoldOut,
    Booking,
    Booked,
}

internal enum HotelPriceAcceptanceFailure
{
    StaleQuote,
    OfferExpired,
    SoldOut,
    Booked,
}

/// <summary>
/// A snapshot of the ONE hotel offer a customer selected (ADR 0030): supplier-neutral port types plus the adapter's
/// opaque offer token, stored verbatim. Not a booking: the price and availability are revalidated before booking, and a
/// changed price is a quote the customer must accept (F-01). Only a signed-in owner's selection can be ordered (Q8).
/// </summary>
internal sealed class HotelSelection
{
    public const int MaxCustomerIdLength = 64;
    public const int MaxTextLength = 200;

    private HotelSelection()
    {
    }

    public Guid Id { get; private set; }

    public string? CustomerId { get; private set; }

    public Guid SearchId { get; private set; }

    public Guid OfferId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string ProviderOfferToken { get; private set; } = string.Empty;

    // The stay.
    public string Destination { get; private set; } = string.Empty;

    public DateOnly CheckIn { get; private set; }

    public DateOnly CheckOut { get; private set; }

    public int Adults { get; private set; }

    /// <summary>The children's ages, comma-separated (none: empty).</summary>
    public string ChildAges { get; private set; } = string.Empty;

    // The property and room, as last seen.
    public string PropertyId { get; private set; } = string.Empty;

    public string PropertyName { get; private set; } = string.Empty;

    public string AddressLine { get; private set; } = string.Empty;

    public string CityCode { get; private set; } = string.Empty;

    public string CountryCode { get; private set; } = string.Empty;

    public decimal? StarRating { get; private set; }

    public string TimeZone { get; private set; } = string.Empty;

    public string RoomDescription { get; private set; } = string.Empty;

    public BoardBasis Board { get; private set; }

    // The rate, as last seen (at selection, then at each revalidation).
    public Money TotalPrice { get; private set; }

    public Money? FeesAtProperty
    {
        get => ToMoney(FeesAmount, FeesCurrency);
        private set => (FeesAmount, FeesCurrency) = FromMoney(value);
    }

    public bool Refundable { get; private set; }

    public DateTimeOffset? FreeCancellationUntil { get; private set; }

    public Money? PenaltyAfterDeadline
    {
        get => ToMoney(PenaltyAmount, PenaltyCurrency);
        private set => (PenaltyAmount, PenaltyCurrency) = FromMoney(value);
    }

    public CancellationPolicy Cancellation => new(Refundable, FreeCancellationUntil, PenaltyAfterDeadline);

    public DateTimeOffset OfferExpiresAt { get; private set; }

    public DateTimeOffset SelectedAt { get; private set; }

    public HotelSelectionStatus Status { get; private set; }

    public DateTimeOffset? RevalidatedAt { get; private set; }

    /// <summary>The price confirmed by the supplier AND agreed by the customer.</summary>
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

    public Guid? PriceQuoteId { get; private set; }

    /// <summary>The quote also changes the room, board or cancellation terms (not only the price): shown to the customer.</summary>
    public bool TermsChanged { get; private set; }

    public Guid? AcceptedPriceQuoteId { get; private set; }

    public DateTimeOffset? PriceAcceptedAt { get; private set; }

    public Money AgreedPrice => ConfirmedPrice ?? TotalPrice;

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    public bool IsAvailable => Status is not (HotelSelectionStatus.Expired or HotelSelectionStatus.SoldOut or HotelSelectionStatus.Booking or HotelSelectionStatus.Booked);

    /// <summary>Its booking was sent (or made): frozen, never revalidated or repriced again.</summary>
    public bool IsFrozen => Status is HotelSelectionStatus.Booking or HotelSelectionStatus.Booked;

    private decimal? FeesAmount { get; set; }

    private CurrencyCode? FeesCurrency { get; set; }

    private decimal? PenaltyAmount { get; set; }

    private CurrencyCode? PenaltyCurrency { get; set; }

    private decimal? ConfirmedAmount { get; set; }

    private CurrencyCode? ConfirmedCurrency { get; set; }

    private decimal? QuotedAmount { get; set; }

    private CurrencyCode? QuotedCurrency { get; set; }

    /// <summary>Takes the snapshot. The offer must still be valid at <paramref name="now"/>.</summary>
    public static HotelSelection Select(Guid searchId, Guid offerId, HotelSearchCriteria criteria, HotelOffer offer, DateTimeOffset now, string? customerId)
    {
        if (offer.ExpiresAt <= now)
        {
            throw new InvalidOperationException("An expired offer cannot be selected.");
        }

        if (customerId is not null && (customerId.Length is 0 or > MaxCustomerIdLength || string.IsNullOrWhiteSpace(customerId)))
        {
            throw new ArgumentException("A customer id is 1 to 64 characters.", nameof(customerId));
        }

        var selection = new HotelSelection
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            SearchId = searchId,
            OfferId = offerId,
            Destination = criteria.Destination,
            CheckIn = criteria.CheckIn,
            CheckOut = criteria.CheckOut,
            Adults = criteria.Adults,
            ChildAges = string.Join(',', criteria.ChildAges),
            SelectedAt = now,
            Status = HotelSelectionStatus.Selected,
        };
        selection.Apply(offer);
        return selection;
    }

    public IReadOnlyList<int> ChildAgeList => [.. ChildAges.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

    public bool IsVisibleTo(string? customerId) => CustomerId is null || string.Equals(CustomerId, customerId, StringComparison.Ordinal);

    public bool IsOwnedBy(string? customerId) => CustomerId is not null && string.Equals(CustomerId, customerId, StringComparison.Ordinal);

    /// <summary>
    /// Applies the supplier's current offer. The same price AND the same room, board and cancellation terms confirm it;
    /// any other price, or changed terms at the same price, become a quote the customer must accept (F-01): nothing the
    /// customer agreed to changes silently. The caller ensures it is the same property.
    /// </summary>
    public void Revalidate(HotelOffer current, DateTimeOffset now)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"A {Status} selection cannot be revalidated.");
        }

        if (current.Reference.ProviderId != ProviderId)
        {
            throw new InvalidOperationException("A revalidated offer must come from the provider that offered it.");
        }

        var agreed = AgreedPrice;
        // Changed against the terms last seen; a pending terms quote stays one until the customer accepts it.
        var termsChanged = RoomDescription != Bounded(current.RoomDescription) || Board != current.Board || Cancellation != current.Cancellation;
        var termsUnaccepted = termsChanged || (Status is HotelSelectionStatus.PriceChanged && TermsChanged);
        Apply(current, keepPrice: true);
        RevalidatedAt = now;
        if (current.TotalPrice == agreed && !termsUnaccepted)
        {
            Status = HotelSelectionStatus.Confirmed;
            ConfirmedPrice = agreed;
            QuotedPrice = null;
            PriceQuoteId = null;
            TermsChanged = false;
            return;
        }

        // The same quote (price and terms) keeps its id, so a customer accepting it in another tab is not sent round a loop.
        if (Status is not HotelSelectionStatus.PriceChanged || QuotedPrice != current.TotalPrice || termsChanged)
        {
            PriceQuoteId = Guid.NewGuid();
        }

        Status = HotelSelectionStatus.PriceChanged;
        QuotedPrice = current.TotalPrice;
        TermsChanged = termsUnaccepted;
    }

    /// <summary>The customer accepts the quoted price by its id; replaying the accepted quote succeeds again.</summary>
    public Result<HotelSelection, HotelPriceAcceptanceFailure> AcceptPrice(Guid priceQuoteId, DateTimeOffset now)
    {
        if (IsFrozen)
        {
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Failure(HotelPriceAcceptanceFailure.Booked);
        }

        if (Status is HotelSelectionStatus.Expired)
        {
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Failure(HotelPriceAcceptanceFailure.OfferExpired);
        }

        if (Status is HotelSelectionStatus.SoldOut)
        {
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Failure(HotelPriceAcceptanceFailure.SoldOut);
        }

        if (Status is HotelSelectionStatus.Confirmed && AcceptedPriceQuoteId == priceQuoteId)
        {
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Success(this);
        }

        if (Status is not HotelSelectionStatus.PriceChanged || PriceQuoteId != priceQuoteId)
        {
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Failure(HotelPriceAcceptanceFailure.StaleQuote);
        }

        if (OfferExpiresAt <= now)
        {
            MarkUnavailable(HotelSelectionStatus.Expired);
            return Result<HotelSelection, HotelPriceAcceptanceFailure>.Failure(HotelPriceAcceptanceFailure.OfferExpired);
        }

        Status = HotelSelectionStatus.Confirmed;
        ConfirmedPrice = QuotedPrice;
        AcceptedPriceQuoteId = priceQuoteId;
        PriceAcceptedAt = now;
        QuotedPrice = null;
        PriceQuoteId = null;
        TermsChanged = false;
        return Result<HotelSelection, HotelPriceAcceptanceFailure>.Success(this);
    }

    /// <summary>The booking is about to be sent: only a Confirmed selection, which is frozen from now on. True when it changed.</summary>
    public bool StartBooking()
    {
        if (Status is not HotelSelectionStatus.Confirmed)
        {
            return false;
        }

        Status = HotelSelectionStatus.Booking;
        return true;
    }

    /// <summary>
    /// The supplier holds the booking (found by booking or by a lookup). From Booking; from Confirmed only for a booking
    /// sent before selections were frozen. Again is a no-op. True when it changed.
    /// </summary>
    public bool MarkBooked()
    {
        if (Status is not (HotelSelectionStatus.Booking or HotelSelectionStatus.Confirmed))
        {
            return false;
        }

        Status = HotelSelectionStatus.Booked;
        return true;
    }

    /// <summary>F-02 or F-03: the offer can no longer be booked. Terminal; the customer searches again.</summary>
    public void MarkUnavailable(HotelSelectionStatus reason)
    {
        if (reason is not (HotelSelectionStatus.Expired or HotelSelectionStatus.SoldOut))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only Expired or SoldOut make a selection unavailable.");
        }

        if (!IsAvailable)
        {
            return;
        }

        Status = reason;
        QuotedPrice = null;
        PriceQuoteId = null;
    }

    private void Apply(HotelOffer offer, bool keepPrice = false)
    {
        ProviderId = offer.Reference.ProviderId;
        ProviderOfferToken = offer.Reference.Value; // a supplier may issue a new offer at revalidation: later steps use it
        PropertyId = offer.Property.Id; // well-formed (bounded) by the port check: an id is never truncated
        PropertyName = Bounded(offer.Property.Name);
        AddressLine = Bounded(offer.Property.AddressLine);
        CityCode = offer.Property.CityCode;
        CountryCode = offer.Property.CountryCode;
        StarRating = offer.Property.StarRating;
        TimeZone = offer.Property.TimeZone;
        RoomDescription = Bounded(offer.RoomDescription);
        Board = offer.Board;
        FeesAtProperty = offer.FeesAtProperty;
        Refundable = offer.Cancellation.Refundable;
        FreeCancellationUntil = offer.Cancellation.FreeCancellationUntil;
        PenaltyAfterDeadline = offer.Cancellation.PenaltyAfterDeadline;
        OfferExpiresAt = offer.ExpiresAt;
        if (!keepPrice)
        {
            TotalPrice = offer.TotalPrice;
        }
    }

    private static string Bounded(string value) => value.Length <= MaxTextLength ? value : value[..MaxTextLength];

    private static Money? ToMoney(decimal? amount, CurrencyCode? currency) =>
        amount is { } a && currency is { } c ? new Money(a, c) : null;

    private static (decimal?, CurrencyCode?) FromMoney(Money? money) => (money?.Amount, money?.Currency);
}
