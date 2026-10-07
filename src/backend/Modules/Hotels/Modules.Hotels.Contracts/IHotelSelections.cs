using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Hotels.Contracts;

/// <summary>
/// What other modules may know about a customer's hotel selection (ADR 0002: Contracts only; ADR 0030). Orders use it to
/// create an order item from a persisted, revalidated selection. The supplier's offer token never leaves Hotels, and the
/// price always comes from here, never from a client (booking rules).
/// </summary>
public interface IHotelSelections
{
    /// <summary>
    /// The selection, if it may be ordered now by this customer: owned by them (made while signed in), revalidated and
    /// Confirmed at an agreed price, and not expired. Another customer's selection, or an anonymous one, is NotFound.
    /// </summary>
    Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Revalidates the selection with the supplier now (a read, safe to repeat) and returns it if it may be booked: the
    /// check right before payment. A changed price or changed terms are saved as a quote for the customer to accept
    /// (NeedsPriceCheck); once accepted, the agreed price is the new one, with its consent evidence.
    /// </summary>
    Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken);
}

/// <summary>
/// A confirmed hotel selection: the price and cancellation terms the customer agreed to, until the supplier's offer expires, with the consent
/// evidence when that price (or its terms) was an accepted change. The guests are counted as travellers are counted (by
/// age at check-out: under 2 an infant, 2 to 11 a child, otherwise an adult), so that each guest can be named.
/// </summary>
public sealed record BookableHotelSelection(
    Guid SelectedOfferId,
    Money AgreedTotalPrice,
    DateTimeOffset OfferExpiresAt,
    Guid? AcceptedPriceQuoteId,
    DateTimeOffset? PriceAcceptedAt,
    int Adults,
    int Children,
    int Infants,
    DateOnly CheckIn,
    DateOnly CheckOut,
    HotelCancellationTerms Cancellation);

/// <summary>
/// The rate's cancellation terms the customer agreed to (ADR 0030 §7): non-refundable, or free until a deadline (an
/// instant), after which <paramref name="PenaltyAfterDeadline"/> is charged (the whole price when not stated).
/// </summary>
public sealed record HotelCancellationTerms(bool Refundable, DateTimeOffset? FreeCancellationUntil, Money? PenaltyAfterDeadline);

public enum HotelSelectionUnavailable
{
    NotFound,

    /// <summary>Not yet revalidated, or a changed price or terms await the customer's acceptance (F-01, F-53).</summary>
    NeedsPriceCheck,

    /// <summary>F-02: search again.</summary>
    Expired,

    /// <summary>F-03: search again.</summary>
    SoldOut,

    /// <summary>The supplier could not answer, or another request changed the selection at the same time: try again.</summary>
    TryAgain,

    /// <summary>The supplier that made this offer is not composed now: checkout must not take payment for it.</summary>
    SupplierCannotBook,
}
