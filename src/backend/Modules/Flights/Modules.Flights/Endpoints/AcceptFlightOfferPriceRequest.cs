using System.ComponentModel.DataAnnotations;

namespace TravelBooking.Modules.Flights.Endpoints;

/// <summary>
/// Accepts a changed price (F-01). Only the quote id from the <c>price-changed</c> problem is sent, never a price:
/// the server applies the quote it stored. Public because the .NET 10 validation source generator skips internal types.
/// </summary>
public sealed class AcceptFlightOfferPriceRequest
{
    [Required]
    public Guid? PriceQuoteId { get; init; }
}
