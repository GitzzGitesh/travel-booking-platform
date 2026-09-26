using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Flights.Contracts;

/// <summary>
/// What other modules may know about a customer's flight selection (ADR 0002: Contracts only). Orders use it to create
/// an order item from a persisted, revalidated selection. The supplier's offer token never leaves Flights, and the price
/// always comes from here, never from a client (booking rules).
/// </summary>
public interface IFlightSelections
{
    /// <summary>
    /// The selection, if it may be ordered now: revalidated and Confirmed at an agreed price, and not expired.
    /// </summary>
    Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, CancellationToken cancellationToken);
}

/// <summary>
/// A confirmed selection: the price the customer agreed to, until the supplier's offer expires. When that price was an
/// accepted change (F-01), the accepted quote and its time are the consent evidence; both are null otherwise.
/// </summary>
public sealed record BookableFlightSelection(
    Guid SelectedOfferId,
    Money AgreedTotalPrice,
    DateTimeOffset OfferExpiresAt,
    Guid? AcceptedPriceQuoteId,
    DateTimeOffset? PriceAcceptedAt);

public enum FlightSelectionUnavailable
{
    NotFound,

    /// <summary>Not yet revalidated, or a changed price awaits the customer's acceptance (F-01).</summary>
    NeedsPriceCheck,

    /// <summary>F-02: search again.</summary>
    Expired,

    /// <summary>F-03: search again.</summary>
    SoldOut,
}
