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
    string? CorrelationId,
    IReadOnlyList<BookedItem>? Items = null) : IIntegrationEvent;

/// <summary>
/// A confirmed item of a settled order (ADR 0030): its product ("Flight" or "Hotel"), the selection it booked (to read
/// the stay's non-personal facts through Hotels.Contracts) and the supplier's booking reference. For a hotel stay, the
/// cancellation terms the customer agreed to, from the order (what a cancellation is refunded by); the penalty is in the
/// item's currency (<paramref name="Currency"/>).
/// </summary>
public sealed record BookedItem(
    string Product, Guid SelectedOfferId, string BookingReference,
    bool? CancellationRefundable = null, DateTimeOffset? FreeCancellationUntil = null, decimal? CancellationPenalty = null, string? Currency = null);
