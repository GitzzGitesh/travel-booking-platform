using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>
/// Orders will not book on this payment: its offer expired, or the held amount is not the order's total. Payments
/// releases the hold (void, or cancel an unfinished challenge) once the payment's outcome is known. Published through the
/// Orders outbox in the same transaction as the order's timeline note (ADR 0007).
/// </summary>
public sealed record OrderPaymentReleaseRequested(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid PaymentId, string Reason, string? CorrelationId) : IIntegrationEvent;
