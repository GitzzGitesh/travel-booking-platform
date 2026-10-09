using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Payments.Contracts;

/// <summary>
/// A refund's final outcome (ADR 0027): the money was returned (<paramref name="Succeeded"/>), or the provider could not
/// return it. Published through the Payments outbox in the same transaction as the outcome. An outcome still unknown is
/// never published: it is looked up, and goes to a person if it stays unknown.
/// </summary>
[IntegrationEventName("payments.PaymentRefundSettled")]
public sealed record PaymentRefundSettled(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid PaymentId, Guid RefundId, Money Amount, bool Succeeded, string? CorrelationId) : IIntegrationEvent;
