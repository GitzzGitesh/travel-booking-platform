using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Infrastructure;

internal sealed class SqlOrderStore(OrdersDbContext db) : IOrderStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        Load().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
        Load().SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId, cancellationToken);

    public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        Load().AsNoTracking().SingleOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.Items.Any(i => i.SelectedOfferId == selectedOfferId))
            .Select(o => (Guid?)o.Id)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken)
    {
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
        where TEvent : IIntegrationEvent =>
        db.Set<OutboxMessage>().Add(OutboxMessage.From(integrationEvent, correlationId));

    public Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var type = typeof(OrderPaymentReleaseRequested).FullName!;
        var payment = paymentId.ToString();
        return db.Set<OutboxMessage>().AnyAsync(
            m => m.Type == type && m.ProcessedAt == null && m.FailedAt == null && m.Payload.Contains(payment), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Set<FlightOrderItem>().AsNoTracking()
            .Where(i => i.Status == FlightOrderItemStatus.AwaitingPayment && i.OfferExpiresAt <= now)
            .GroupBy(i => EF.Property<Guid>(i, "OrderId"))
            .OrderBy(g => g.Min(i => i.OfferExpiresAt))
            .Select(g => g.Key)
            .Take(limit)
            .ToListAsync(cancellationToken);

    private IQueryable<Order> Load() => db.Orders.Include(o => o.Items).Include(o => o.Timeline).AsSplitQuery();
}
