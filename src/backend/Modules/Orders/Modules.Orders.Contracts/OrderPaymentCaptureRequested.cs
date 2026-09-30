using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>
/// The order's bookings are settled and at least one is confirmed: Payments charges <paramref name="Amount"/> (the confirmed
/// items' agreed prices) on the order's authorized payment (ADR 0005: capture only after a confirmed booking; a partial
/// capture releases the rest). Published through the Orders outbox in the same transaction as the confirmation, so a
/// crash between confirming and charging never loses the charge (F-25).
/// </summary>
public sealed record OrderPaymentCaptureRequested(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid PaymentId, Money Amount, string? CorrelationId) : IIntegrationEvent;
