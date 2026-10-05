using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>Persistence port for orders; implemented in Infrastructure (architecture rules).</summary>
/// <summary>
/// The order kept changing while it was being read, so no consistent copy could be loaded (it is read again a few times
/// first). Nothing was changed: the caller may answer "try again", and must not decide anything from a partial read.
/// </summary>
internal sealed class OrderKeptChangingException(Guid orderId) : BuildingBlocks.TryAgainException($"Order {orderId} kept changing while it was being loaded.")
{
    public Guid OrderId { get; } = orderId;
}

/// <remarks>
/// The loads (<see cref="FindAsync"/>, <see cref="FindOwnedAsync"/>, <see cref="FindByIdempotencyKeyAsync"/>) return a
/// consistent order or throw <see cref="OrderKeptChangingException"/>. A tracked load needs a unit of work without pending
/// changes (load first, then change).
/// </remarks>
internal interface IOrderStore
{
    Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The customer's own order, for a change; null if it does not exist OR belongs to someone else (no IDOR).</summary>
    Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken);

    Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The order that already books this flight selection, if any.</summary>
    Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken);

    /// <summary>Adds the order; false if its idempotency key or selection is already used (unique constraints).</summary>
    Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>Saves a loaded order; false if another request changed it first (optimistic concurrency).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>Adds an integration event to the Orders outbox, saved atomically with the next <see cref="TrySaveAsync"/> (ADR 0007).</summary>
    void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
        where TEvent : IIntegrationEvent;

    /// <summary>Whether a release request for this payment is still waiting in the outbox (neither delivered nor given up).</summary>
    Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Orders with an item still awaiting payment whose offer expired at or before <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Orders with a booking to look up now: an item PendingConfirmation, or one still Booking since before
    /// <paramref name="startedBefore"/> (interrupted), whose next lookup (backoff) is due at <paramref name="now"/>; the
    /// most overdue first, so one supplier outage never starves newer bookings.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindBookingsToReconcileAsync(DateTimeOffset startedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Operations queue: orders with an item in <paramref name="status"/>, oldest first, created after
    /// <paramref name="after"/> in (CreatedAt, Id) order (the cursor: no order is skipped on ties), with their items, read-only.
    /// </summary>
    Task<IReadOnlyList<Order>> FindWithItemStatusAsync(FlightOrderItemStatus status, (DateTimeOffset CreatedAt, Guid Id)? after, int limit, CancellationToken cancellationToken);

    /// <summary>Adds an audit entry, saved atomically with the next <see cref="TrySaveAsync"/> (ADR 0022).</summary>
    void Audit(BuildingBlocks.Audit.AuditEntry entry);
}

/// <summary><paramref name="CustomerId"/> is the authenticated customer (Q8); the actor recorded on the timeline.</summary>
internal sealed record CreateFlightOrder(string CustomerId, string IdempotencyKey, Guid SelectedOfferId, string? CorrelationId);

internal sealed record CreatedOrder(Order Order, bool Created);

internal abstract record CreateFlightOrderFailure
{
    private CreateFlightOrderFailure()
    {
    }

    internal sealed record InvalidIdempotencyKey : CreateFlightOrderFailure;

    /// <summary>No authenticated customer: orders need a signed-in customer (Q8).</summary>
    internal sealed record CustomerRequired : CreateFlightOrderFailure;

    /// <summary>The key was already used for a different selection (409: never a second effect).</summary>
    internal sealed record IdempotencyKeyReused : CreateFlightOrderFailure;

    /// <summary>
    /// The caller already has an order for this selection (under another key): at most one booking per selection.
    /// Selections belong to one customer, so another customer's order is never reported here.
    /// </summary>
    internal sealed record SelectionAlreadyOrdered(Guid OrderId) : CreateFlightOrderFailure;

    internal sealed record SelectionUnavailable(FlightSelectionUnavailable Reason) : CreateFlightOrderFailure;
}

/// <summary>
/// Creates an order for a revalidated, Confirmed flight selection (ADR 0005). Idempotent: the same key and selection
/// return the original order, enforced by unique constraints on the key and on the selection, not by a check alone.
/// No supplier or payment call is made here; the price comes from Flights, never from the client.
/// </summary>
internal sealed class CreateFlightOrderHandler(IFlightSelections selections, IOrderStore store, TimeProvider timeProvider)
{
    public const int MaxIdempotencyKeyLength = 100;

    public async Task<Result<CreatedOrder, CreateFlightOrderFailure>> HandleAsync(CreateFlightOrder command, CancellationToken cancellationToken)
    {
        if (!Order.IsValidCustomerId(command.CustomerId))
        {
            return Failure(new CreateFlightOrderFailure.CustomerRequired());
        }

        if (!IsValidKey(command.IdempotencyKey))
        {
            return Failure(new CreateFlightOrderFailure.InvalidIdempotencyKey());
        }

        if (await Replay(command, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var selection = await selections.GetBookableAsync(command.SelectedOfferId, command.CustomerId, cancellationToken);
        if (!selection.IsSuccess)
        {
            return Failure(new CreateFlightOrderFailure.SelectionUnavailable(selection.Error));
        }

        var bookable = selection.Value;
        var order = Order.CreateForFlight(
            command.CustomerId,
            command.IdempotencyKey,
            bookable.SelectedOfferId,
            bookable.AgreedTotalPrice,
            bookable.OfferExpiresAt,
            bookable is { AcceptedPriceQuoteId: { } quote, PriceAcceptedAt: { } acceptedAt } ? new PriceConsent(quote, acceptedAt) : null,
            new TransitionContext(timeProvider.GetUtcNow(), Actor(command.CustomerId), command.CorrelationId),
            bookable.LastTravelDate is { } lastTravelDate
                ? new TravellerNeeds(bookable.Adults, bookable.Children, bookable.Infants, bookable.DocumentsRequired, lastTravelDate)
                : null);

        if (await store.TryAddAsync(order, cancellationToken))
        {
            return Result<CreatedOrder, CreateFlightOrderFailure>.Success(new CreatedOrder(order, Created: true));
        }

        // A concurrent request won the key or the selection: answer as a replay would. The selection is the caller's
        // own (checked above), so the order holding it is theirs too.
        return await Replay(command, cancellationToken)
            ?? throw new InvalidOperationException("An order insert conflicted, but neither the key nor the caller's selection is taken.");
    }

    private async Task<Result<CreatedOrder, CreateFlightOrderFailure>?> Replay(CreateFlightOrder command, CancellationToken cancellationToken)
    {
        if (await store.FindByIdempotencyKeyAsync(command.CustomerId, command.IdempotencyKey, cancellationToken) is { } existing)
        {
            return existing.Items.Any(i => i.SelectedOfferId == command.SelectedOfferId)
                ? Result<CreatedOrder, CreateFlightOrderFailure>.Success(new CreatedOrder(existing, Created: false))
                : Failure(new CreateFlightOrderFailure.IdempotencyKeyReused());
        }

        if (await store.FindOrderIdBySelectedOfferAsync(command.SelectedOfferId, cancellationToken) is not { } orderId)
        {
            return null;
        }

        // The order for this selection may have been committed by a concurrent request with OUR key after the key
        // lookup above: look again before calling it someone else's order.
        if (await store.FindByIdempotencyKeyAsync(command.CustomerId, command.IdempotencyKey, cancellationToken) is { } ours && ours.Id == orderId)
        {
            return Result<CreatedOrder, CreateFlightOrderFailure>.Success(new CreatedOrder(ours, Created: false));
        }

        // The caller's own order for this selection is named. Another customer's is never revealed: the request goes on
        // as if there were none, and the selection (theirs, not the caller's) is not found.
        return await store.FindOwnedAsync(orderId, command.CustomerId, cancellationToken) is not null
            ? Failure(new CreateFlightOrderFailure.SelectionAlreadyOrdered(orderId))
            : null;
    }

    /// <summary>The timeline actor for a customer's own action.</summary>
    internal static string Actor(string customerId) => $"customer:{customerId}";

    internal static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= MaxIdempotencyKeyLength } && key.All(c => c is >= '!' and <= '~');

    private static Result<CreatedOrder, CreateFlightOrderFailure> Failure(CreateFlightOrderFailure failure) =>
        Result<CreatedOrder, CreateFlightOrderFailure>.Failure(failure);
}
