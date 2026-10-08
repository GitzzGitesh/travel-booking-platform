namespace TravelBooking.Modules.Hotels.Contracts;

/// <summary>
/// The non-personal facts of a booked (or selected) stay, for the customer's voucher email and the operations views
/// (ADR 0030): the property, the dates, the room and board, and the agreed cancellation terms. A query for internal
/// modules (Notifications, Orders' staff views) by the selection id they hold; never guest data, never supplier tokens.
/// Once the stay is booked its selection is frozen, so these facts are what was booked.
/// </summary>
public interface IHotelStays
{
    /// <summary>The stay of this selection, or null when there is none.</summary>
    Task<HotelStay?> GetStayAsync(Guid selectedOfferId, CancellationToken cancellationToken);
}

/// <param name="TimeZone">The property's IANA time zone: its cancellation deadline is stated in it.</param>
/// <param name="Board">RoomOnly, Breakfast, HalfBoard, FullBoard or AllInclusive.</param>
/// <param name="Booked">The supplier holds the booking (the selection is frozen).</param>
public sealed record HotelStay(
    string PropertyName,
    string AddressLine,
    string CityCode,
    string CountryCode,
    decimal? StarRating,
    string TimeZone,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    string Room,
    string Board,
    HotelCancellationTerms Cancellation,
    bool Booked);
