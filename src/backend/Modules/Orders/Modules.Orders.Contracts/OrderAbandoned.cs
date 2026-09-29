using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>
/// The order ended without a booking: its offer expired unpaid, and no payment attempt for it still holds, or may hold,
/// funds (the expiry job abandons an order only then). Customers shortens the retention of its travellers' personal
/// data (Q9, approved 2026-09-29). Published through the Orders outbox in the same transaction as the abandonment (ADR 0007).
/// </summary>
public sealed record OrderAbandoned(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, string? CorrelationId) : IIntegrationEvent;
