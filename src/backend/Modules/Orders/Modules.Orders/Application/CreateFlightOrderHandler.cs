using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>Persistence port for orders; implemented in Infrastructure (architecture rules).</summary>
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
    /// Another order already books this selection: at most one booking per selection. <paramref name="OrderId"/> is set
    /// only when that order is the same customer's; another customer's order id is never revealed.
    /// </summary>
    internal sealed record SelectionAlreadyOrdered(Guid? OrderId) : CreateFlightOrderFailure;

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

        var selection = await selections.GetBookableAsync(command.SelectedOfferId, cancellationToken);
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
            new TransitionContext(timeProvider.GetUtcNow(), Actor(command.CustomerId), command.CorrelationId));

        if (await store.TryAddAsync(order, cancellationToken))
        {
            return Result<CreatedOrder, CreateFlightOrderFailure>.Success(new CreatedOrder(order, Created: true));
        }

        // A concurrent request won the key or the selection: answer as a replay would.
        return await Replay(command, cancellationToken)
            ?? throw new InvalidOperationException("An order insert conflicted, but neither the key nor the selection is taken.");
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

        var owned = await store.FindOwnedAsync(orderId, command.CustomerId, cancellationToken) is not null;
        return Failure(new CreateFlightOrderFailure.SelectionAlreadyOrdered(owned ? orderId : null));
    }

    /// <summary>The timeline actor for a customer's own action.</summary>
    internal static string Actor(string customerId) => $"customer:{customerId}";

    private static bool IsValidKey(string key) =>
        key is { Length: > 0 and <= MaxIdempotencyKeyLength } && key.All(c => c is >= '!' and <= '~');

    private static Result<CreatedOrder, CreateFlightOrderFailure> Failure(CreateFlightOrderFailure failure) =>
        Result<CreatedOrder, CreateFlightOrderFailure>.Failure(failure);
}
