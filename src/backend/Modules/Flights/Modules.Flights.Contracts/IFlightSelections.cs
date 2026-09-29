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
    /// The selection, if it may be ordered now by this customer: owned by them (made while signed in), revalidated and
    /// Confirmed at an agreed price, and not expired. Another customer's selection, or an anonymous one, is NotFound:
    /// nothing tells whether it exists.
    /// </summary>
    /// <param name="customerId">The signed-in customer's internal id, from a validated token only.</param>
    Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Revalidates the selection with the supplier now (a read, safe to repeat) and returns it if it may be booked: the
    /// check right before payment (booking rules). A changed price is saved as a quote for the customer to accept
    /// (NeedsPriceCheck); once accepted, the agreed price is the new one, with its consent evidence.
    /// </summary>
    /// <param name="customerId">The signed-in customer's internal id: only the selection's owner may revalidate it for checkout.</param>
    Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken);
}

/// <summary>
/// A confirmed selection: the price the customer agreed to, until the supplier's offer expires. When that price was an
/// accepted change (F-01), the accepted quote and its time are the consent evidence; both are null otherwise.
/// </summary>
/// <param name="Adults">The passenger mix travellers must be given for (with <paramref name="Children"/> and <paramref name="Infants"/>).</param>
/// <param name="DocumentsRequired">The supplier requires travel documents for this offer (Q9: only then are they collected).</param>
/// <param name="LastTravelDate">The local date of the last arrival: the retention clock for travellers' personal data (Q9).</param>
public sealed record BookableFlightSelection(
    Guid SelectedOfferId,
    Money AgreedTotalPrice,
    DateTimeOffset OfferExpiresAt,
    Guid? AcceptedPriceQuoteId,
    DateTimeOffset? PriceAcceptedAt,
    int Adults = 1,
    int Children = 0,
    int Infants = 0,
    bool DocumentsRequired = false,
    DateOnly? LastTravelDate = null);

public enum FlightSelectionUnavailable
{
    NotFound,

    /// <summary>Not yet revalidated, or a changed price awaits the customer's acceptance (F-01).</summary>
    NeedsPriceCheck,

    /// <summary>F-02: search again.</summary>
    Expired,

    /// <summary>F-03: search again.</summary>
    SoldOut,

    /// <summary>The supplier could not answer, or another request changed the selection at the same time: nothing changed, try again.</summary>
    TryAgain,

    /// <summary>
    /// The supplier that made this offer cannot book it through us (its adapter does not implement booking, or it is
    /// not composed): checkout must not take payment for it. Search again with a bookable provider.
    /// </summary>
    SupplierCannotBook,
}
