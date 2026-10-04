using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;

namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>What a refund is for (ADR 0027).</summary>
public enum RefundCaseKind
{
    /// <summary>Confirmed bookings cancelled at the supplier: the supplier's refund, less any disclosed fee.</summary>
    Cancellation,

    /// <summary>A refund beyond the supplier's, at our cost: always approved by a second person.</summary>
    Goodwill,
}

/// <summary>
/// An approved refund (ADR 0027): Payments returns <paramref name="Amount"/> of the order's captured payment, once,
/// under our key <c>{RefundId}:refund</c>. The amount was computed by the server and never exceeds what can still be
/// refunded. Published through the Orders outbox in the same transaction as the approval.
/// </summary>
public sealed record OrderRefundRequested(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid PaymentId, Guid RefundId, Money Amount, string? CorrelationId) : IIntegrationEvent;

/// <summary>
/// Confirmed bookings of the order were cancelled at the supplier (ADR 0027), recorded with their refund case: published in
/// the same save, for the customer's notice. <paramref name="ExpectedRefund"/> is the refund the server computed: not a
/// promise (it still needs a second person's approval; zero when nothing is refunded), so it is never told to the customer.
/// </summary>
public sealed record OrderCancellationRecorded(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid RefundCaseId, Money ExpectedRefund, string? CorrelationId) : IIntegrationEvent;

/// <summary>A customer asked to cancel a booking (ADR 0029): for the customer's acknowledgement. Nothing is cancelled yet.</summary>
public sealed record CustomerCancellationRequested(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid RequestId, string? CorrelationId) : IIntegrationEvent;

/// <summary>A customer's cancellation request was declined by a person (ADR 0029): the customer is told support will contact them.</summary>
public sealed record CustomerCancellationDeclined(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid RequestId, string? CorrelationId) : IIntegrationEvent;
