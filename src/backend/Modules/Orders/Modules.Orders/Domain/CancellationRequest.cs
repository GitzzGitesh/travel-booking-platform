namespace TravelBooking.Modules.Orders.Domain;

internal enum CancellationRequestStatus
{
    /// <summary>Waiting for operations: cancel at the supplier's desk and open the cancellation case, or decline it.</summary>
    Open,

    /// <summary>A cancellation case was opened for the order (ADR 0027): the customer is told by that case's notice.</summary>
    Completed,

    /// <summary>A person found it cannot be cancelled; support contacts the customer.</summary>
    Declined,

    /// <summary>The customer withdrew it.</summary>
    Withdrawn,
}

/// <summary>
/// A customer's wish to cancel a booking (ADR 0029). It changes no booking and promises no refund: operations act on it
/// with the cancellation case of ADR 0027. One per customer and idempotency key; at most one open per order (both
/// enforced by the database).
/// </summary>
internal sealed class CancellationRequest
{
    public const int MaxKeyLength = 100;
    public const int MaxReasonLength = 200;

    private CancellationRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public CancellationRequestStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>Who resolved it: "customer:{id}" (withdrawn) or "staff:{id}".</summary>
    public string? ResolvedBy { get; private set; }

    /// <summary>The staff ticket reference (declined), or the refund case that completed it; never shown to the customer.</summary>
    public string? ResolutionNote { get; private set; }

    public bool IsOpen => Status is CancellationRequestStatus.Open;

    public static CancellationRequest Open(Guid orderId, string customerId, string idempotencyKey, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = orderId,
        CustomerId = customerId,
        IdempotencyKey = idempotencyKey,
        Status = CancellationRequestStatus.Open,
        RequestedAt = at,
    };

    /// <summary>Completed by the cancellation case opened for the order; false when it was no longer open.</summary>
    public bool Complete(Guid refundCaseId, string staff, DateTimeOffset at) =>
        Resolve(CancellationRequestStatus.Completed, staff, $"refund-case:{refundCaseId}", at);

    public bool Decline(string staff, string reason, DateTimeOffset at) => Resolve(CancellationRequestStatus.Declined, staff, reason, at);

    public bool Withdraw(DateTimeOffset at) => Resolve(CancellationRequestStatus.Withdrawn, $"customer:{CustomerId}", null, at);

    private bool Resolve(CancellationRequestStatus status, string actor, string? note, DateTimeOffset at)
    {
        if (!IsOpen)
        {
            return false;
        }

        Status = status;
        ResolvedBy = actor;
        ResolutionNote = note;
        ResolvedAt = at;
        return true;
    }
}
