using System.ComponentModel.DataAnnotations;

namespace TravelBooking.Modules.Flights.Endpoints;

/// <summary>
/// Selects one offer from a search. Public because the .NET 10 validation source generator skips internal types
/// (ADR 0003). Both ids come from <c>POST /flights/searches</c>; nothing about the offer or its price is sent.
/// </summary>
public sealed class SelectFlightOfferRequest
{
    [Required]
    public Guid? SearchId { get; init; }

    [Required]
    public Guid? OfferId { get; init; }
}
