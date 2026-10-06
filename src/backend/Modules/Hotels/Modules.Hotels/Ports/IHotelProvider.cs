using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;

namespace TravelBooking.Modules.Hotels.Ports;

/// <summary>
/// The hotel supplier port (ADR 0004, ADR 0030). Implemented by Integrations.Hotels.* adapters, which map supplier models
/// to these types. Booking, lookup and cancellation are added with the batches that use them (H2, H4). No real supplier
/// is assumed: the port is validated against a real supplier shape when one is chosen (Q6).
/// </summary>
public interface IHotelProvider
{
    /// <summary>Stable provider identifier, recorded on every offer reference.</summary>
    string Id { get; }

    /// <summary>
    /// Searches for prepaid room offers (ADR 0030: we sell pay-now rates only). An idempotent read, so it may be
    /// retried on transient failures. No offers is a valid result. Failures are returned, never thrown.
    /// </summary>
    Task<Result<HotelSearchResult, ProviderError>> SearchAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// The offer as the supplier prices it NOW, by its opaque reference (an idempotent read). A different price is for
    /// the core to handle (F-01), never passed on silently. No longer bookable is
    /// <see cref="ProviderErrorKind.OfferExpired"/> or <see cref="ProviderErrorKind.SoldOut"/>. The returned reference
    /// replaces the one revalidated for every later operation.
    /// </summary>
    Task<Result<HotelOffer, ProviderError>> RevalidateAsync(HotelOfferRef offer, CancellationToken cancellationToken);

    /// <summary>
    /// Books the offer under OUR client reference. A WRITE: never retried blindly (booking rules). Adapters MUST
    /// guarantee at most one booking per client reference: by the supplier's own idempotency where it has it, otherwise
    /// by looking the reference up before booking. A repeat returns the existing booking or an error, never a second one.
    /// Failures are RETURNED, never thrown. Only <see cref="ProviderErrorKind.Rejected"/>,
    /// <see cref="ProviderErrorKind.PriceChanged"/>, <see cref="ProviderErrorKind.SoldOut"/>,
    /// <see cref="ProviderErrorKind.OfferExpired"/> and <see cref="ProviderErrorKind.InvalidRequest"/> mean the supplier
    /// definitely did not book; anything else is an unknown outcome, settled by <see cref="RetrieveBookingAsync"/>.
    /// </summary>
    Task<Result<HotelBookingConfirmation, ProviderError>> BookAsync(HotelBookingDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a booking up by OUR reference: mandatory for reconciliation. An idempotent read. "Not found" is a success
    /// without a booking; an error means the answer is still unknown.
    /// </summary>
    Task<Result<HotelBookingLookup, ProviderError>> RetrieveBookingAsync(string clientReference, CancellationToken cancellationToken);
}

/// <summary>A provider's opaque offer token, with the provider that issued it. The core stores it verbatim and never parses it.</summary>
public sealed record HotelOfferRef(string ProviderId, string Value);

/// <summary>A search result; a wrapper so that search-level data can be added without a port break.</summary>
public sealed record HotelSearchResult(IReadOnlyList<HotelOffer> Offers);

/// <summary>
/// One room for a stay (ADR 0030: one room per booking at launch), with each child's age at check-out (the date traveller
/// details are checked against). The destination is an IATA city code; check-in and
/// check-out are the property's local dates.
/// </summary>
public sealed record HotelSearchCriteria(string Destination, DateOnly CheckIn, DateOnly CheckOut, int Adults, IReadOnlyList<int> ChildAges)
{
    public const int MaxAdults = 4;
    public const int MaxChildren = 3;
    public const int MaxChildAge = 17;
    public const int MaxNights = 30;

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    /// <summary>The shape rules a supplier relies on (the endpoint validates the request first; this guards the port).</summary>
    public bool IsWellFormed() =>
        Destination is { Length: 3 } && Destination.All(char.IsAsciiLetterUpper)
        && Nights is >= 1 and <= MaxNights
        && Adults is >= 1 and <= MaxAdults
        && ChildAges.Count <= MaxChildren && ChildAges.All(age => age is >= 0 and <= MaxChildAge);
}

/// <summary>The property, as the supplier describes it. Its id is supplier-scoped and never shown to customers.</summary>
/// <param name="StarRating">0 to 5 in half steps; null when the supplier does not rate it.</param>
/// <param name="TimeZone">The property's IANA time zone: cancellation deadlines are shown in it.</param>
public sealed record HotelProperty(
    string Id, string Name, string AddressLine, string CityCode, string CountryCode, decimal? StarRating, string TimeZone)
{
    public const int MaxIdLength = 200;
    public const int MaxTimeZoneLength = 64;

    /// <summary>
    /// What we can store and show honestly: an id within its column (an id is never truncated), a name, IATA city and
    /// ISO country codes, a star rating of 0 to 5 in half steps (or none), and a known IANA time zone.
    /// </summary>
    public bool IsWellFormed() =>
        Id is { Length: > 0 and <= MaxIdLength } && !string.IsNullOrWhiteSpace(Name)
        && CityCode is { Length: 3 } && CityCode.All(char.IsAsciiLetterUpper)
        && CountryCode is { Length: 2 } && CountryCode.All(char.IsAsciiLetterUpper)
        && (StarRating is null || (StarRating is >= 0 and <= 5 && StarRating * 2 == decimal.Truncate(StarRating.Value * 2)))
        && TimeZone is { Length: > 0 and <= MaxTimeZoneLength } && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _);
}

public enum BoardBasis
{
    RoomOnly,
    Breakfast,
    HalfBoard,
    FullBoard,
    AllInclusive,
}

/// <summary>
/// The rate's cancellation terms as the supplier states them: non-refundable, or refundable without charge until a
/// deadline (an instant), after which <see cref="PenaltyAfterDeadline"/> is charged (the whole price when not stated).
/// </summary>
public sealed record CancellationPolicy(bool Refundable, DateTimeOffset? FreeCancellationUntil, Money? PenaltyAfterDeadline)
{
    public static CancellationPolicy NonRefundable { get; } = new(false, null, null);

    public static CancellationPolicy FreeUntil(DateTimeOffset deadline, Money? penaltyAfter) => new(true, deadline, penaltyAfter);

    public bool IsWellFormed(Money total) =>
        Refundable
            ? FreeCancellationUntil is not null
              && (PenaltyAfterDeadline is not { } penalty || (penalty.Currency == total.Currency && penalty.Amount >= 0 && penalty.Amount <= total.Amount))
            : FreeCancellationUntil is null && PenaltyAfterDeadline is null;
}

/// <summary>
/// A prepaid room offer for the stay: the total payable now (taxes and fees we collect included), fees payable at the
/// property itself (information only, never charged by us), the cancellation terms and the offer's expiry.
/// </summary>
public sealed record HotelOffer(
    HotelOfferRef Reference,
    HotelProperty Property,
    string RoomDescription,
    BoardBasis Board,
    Money TotalPrice,
    Money? FeesAtProperty,
    CancellationPolicy Cancellation,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Sellable and showable as stated: a positive total, a well-formed property and cancellation terms, fees in the same currency.</summary>
    public bool IsSellable() =>
        TotalPrice.Amount > 0 && Property.IsWellFormed() && Cancellation.IsWellFormed(TotalPrice) && Enum.IsDefined(Board)
        && !string.IsNullOrWhiteSpace(RoomDescription) && (FeesAtProperty is not { } fees || (fees.Currency == TotalPrice.Currency && fees.Amount >= 0));
}
