using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>How an order's booking ended, for the customer (ADR 0024).</summary>
public enum BookingOutcome
{
    /// <summary>Every item is booked; the confirmed items' agreed prices are charged.</summary>
    Confirmed,

    /// <summary>Some items are booked (and only those are charged); the others were not.</summary>
    PartiallyConfirmed,

    /// <summary>Nothing was booked: nothing is charged, and the held funds are released.</summary>
    NotBooked,
}

/// <summary>
/// The order's booking is settled: published once, in the same transaction as the payment settlement (the capture or the
/// release of the hold), so the customer is told exactly what happened to their money. It carries only non-personal facts:
/// the supplier references of the booked items and the amount charged, from the server-side order (never recomputed).
/// </summary>
public sealed record OrderBookingSettled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    BookingOutcome Outcome,
    IReadOnlyList<string> BookingReferences,
    Money? Charged,
    string? CorrelationId) : IIntegrationEvent;
