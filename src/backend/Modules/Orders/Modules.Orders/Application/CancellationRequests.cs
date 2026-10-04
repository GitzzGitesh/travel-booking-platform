using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

internal interface ICancellationRequestStore
{
    void Add(CancellationRequest request);

    /// <summary>Tracked, for a change. Load the order first: an order load that retries clears the change tracker.</summary>
    Task<CancellationRequest?> FindAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Read-only: which order a request belongs to, before the order is loaded.</summary>
    Task<CancellationRequest?> PeekAsync(Guid requestId, CancellationToken cancellationToken);

    Task<CancellationRequest?> FindByKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The order's open request, tracked (at most one: a filtered unique index).</summary>
    Task<CancellationRequest?> FindOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The order's latest request, whatever its status, read-only.</summary>
    Task<CancellationRequest?> FindLatestForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Open requests, oldest first (the operations list).</summary>
    Task<IReadOnlyList<CancellationRequest>> FindOpenAsync(int limit, CancellationToken cancellationToken);
}

internal enum CancellationRequestOutcome
{
    Done,

    /// <summary>The same key for the same order again: the request it opened.</summary>
    Replayed,

    /// <summary>The same key for another order: refused.</summary>
    IdempotencyConflict,
    Invalid,
    NotFound,

    /// <summary>The order has no confirmed booking to cancel.</summary>
    NotCancellable,

    /// <summary>The order already has an open request (returned with it).</summary>
    AlreadyRequested,

    /// <summary>The request was already completed, declined or withdrawn.</summary>
    NotOpen,
    Conflict,
}

/// <summary>
/// Customers' cancellation requests (ADR 0029): recorded, acknowledged and shown to operations, never acted on against the
/// supplier here. Operations complete one by opening the order's cancellation case (ADR 0027), or decline it. Every step
/// goes on the order's timeline; staff steps are audited.
/// </summary>
internal sealed class CancellationRequestHandler(IOrderStore orders, ICancellationRequestStore requests, TimeProvider timeProvider)
{
    public const string DeclineAction = "orders.cancellation-request.decline";
    public const int MaxOpen = 100;

    public async Task<(CancellationRequestOutcome Outcome, CancellationRequest? Request)> RequestAsync(
        Guid orderId, string customerId, string? idempotencyKey, string? correlationId, CancellationToken cancellationToken)
    {
        if (!Order.IsValidCustomerId(customerId) || idempotencyKey is not { Length: > 0 and <= CancellationRequest.MaxKeyLength }
            || !idempotencyKey.All(c => c is > ' ' and <= '~'))
        {
            return (CancellationRequestOutcome.Invalid, null);
        }

        if (await Replay(orderId, customerId, idempotencyKey, cancellationToken) is { } replay)
        {
            return replay;
        }

        if (await orders.FindOwnedAsync(orderId, customerId, cancellationToken) is not { } order)
        {
            return (CancellationRequestOutcome.NotFound, null);
        }

        if (!order.Items.Any(i => i.Status is FlightOrderItemStatus.Confirmed))
        {
            return (CancellationRequestOutcome.NotCancellable, null);
        }

        if (await requests.FindOpenForOrderAsync(orderId, cancellationToken) is { } open)
        {
            return (CancellationRequestOutcome.AlreadyRequested, open);
        }

        var now = timeProvider.GetUtcNow();
        var request = CancellationRequest.Open(orderId, customerId, idempotencyKey, now);
        requests.Add(request);
        order.NoteRefund([], $"Cancellation requested by the customer (request {request.Id:N}): for operations",
            new TransitionContext(now, CreateFlightOrderHandler.Actor(customerId), correlationId), null);
        orders.Publish(new CustomerCancellationRequested(Guid.NewGuid(), now, orderId, request.Id, correlationId), correlationId);
        if (await orders.TrySaveAsync(cancellationToken))
        {
            return (CancellationRequestOutcome.Done, request);
        }

        // A concurrent repeat with the same key, or another open request for the order (unique indexes), won.
        return await Replay(orderId, customerId, idempotencyKey, cancellationToken)
            ?? (await requests.FindOpenForOrderAsync(orderId, cancellationToken) is { } winner
                ? (CancellationRequestOutcome.AlreadyRequested, winner)
                : (CancellationRequestOutcome.Conflict, null));
    }

    public async Task<(CancellationRequestOutcome Outcome, CancellationRequest? Request)> WithdrawAsync(
        Guid orderId, Guid requestId, string customerId, string? correlationId, CancellationToken cancellationToken)
    {
        // The request must belong to this customer AND to the order named: nothing changes otherwise. The order is loaded
        // before the tracked request (an order load that retries clears the change tracker).
        if (await requests.PeekAsync(requestId, cancellationToken) is not { } peeked || peeked.CustomerId != customerId || peeked.OrderId != orderId
            || await orders.FindOwnedAsync(orderId, customerId, cancellationToken) is not { } order
            || await requests.FindAsync(requestId, cancellationToken) is not { } request)
        {
            return (CancellationRequestOutcome.NotFound, null); // never says that another customer's request exists
        }

        if (request.Status is CancellationRequestStatus.Withdrawn)
        {
            return (CancellationRequestOutcome.Replayed, request); // a repeated withdrawal: the same answer, nothing new
        }

        var now = timeProvider.GetUtcNow();
        if (!request.Withdraw(now))
        {
            return (CancellationRequestOutcome.NotOpen, request);
        }

        order.NoteRefund([], $"Cancellation request {request.Id:N} withdrawn by the customer",
            new TransitionContext(now, CreateFlightOrderHandler.Actor(customerId), correlationId), null);
        return await orders.TrySaveAsync(cancellationToken) ? (CancellationRequestOutcome.Done, request) : (CancellationRequestOutcome.Conflict, null);
    }

    /// <summary>A person declines it (the booking cannot be cancelled): audited; the customer is told support will contact them.</summary>
    public async Task<(CancellationRequestOutcome Outcome, CancellationRequest? Request)> DeclineAsync(
        Guid requestId, string staffId, string reason, AuditSource source, CancellationToken cancellationToken)
    {
        if (!AuditReasons.IsValid(reason) || staffId is not { Length: > 0 and <= 64 })
        {
            return (CancellationRequestOutcome.Invalid, null);
        }

        if (await requests.PeekAsync(requestId, cancellationToken) is not { } peeked
            || await orders.FindAsync(peeked.OrderId, cancellationToken) is not { } order
            || await requests.FindAsync(requestId, cancellationToken) is not { } request)
        {
            return (CancellationRequestOutcome.NotFound, null);
        }

        var now = timeProvider.GetUtcNow();
        var staff = $"staff:{staffId}";
        if (!request.Decline(staff, reason, now))
        {
            return (CancellationRequestOutcome.NotOpen, request);
        }

        order.NoteRefund([], $"Cancellation request {request.Id:N} declined: {reason}", new TransitionContext(now, staff, source.CorrelationId), null);
        orders.Publish(new CustomerCancellationDeclined(Guid.NewGuid(), now, order.Id, request.Id, source.CorrelationId), source.CorrelationId);
        orders.Audit(AuditEntry.For(source, now, staff, DeclineAction, $"cancellation-request:{request.Id}", nameof(CancellationRequestStatus.Open),
            $"{CancellationRequestStatus.Declined}; {reason}"));
        return await orders.TrySaveAsync(cancellationToken) ? (CancellationRequestOutcome.Done, request) : (CancellationRequestOutcome.Conflict, null);
    }

    private async Task<(CancellationRequestOutcome Outcome, CancellationRequest? Request)?> Replay(
        Guid orderId, string customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        await requests.FindByKeyAsync(customerId, idempotencyKey, cancellationToken) is { } existing
            ? existing.OrderId == orderId ? (CancellationRequestOutcome.Replayed, existing) : (CancellationRequestOutcome.IdempotencyConflict, null)
            : null;
}

/// <summary>A customer's own orders, newest first (ADR 0029: "My trips"), read-only, with their items.</summary>
internal interface IOrderHistory
{
    /// <param name="before">The cursor: orders created before this (CreatedAt, Id), so none is skipped on ties.</param>
    Task<IReadOnlyList<Order>> FindForCustomerAsync(string customerId, (DateTimeOffset CreatedAt, Guid Id)? before, int limit, CancellationToken cancellationToken);
}
