using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Infrastructure;

internal sealed class SqlOrderStore(OrdersDbContext db) : IOrderStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        Load().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<Order?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Load().AsNoTracking().SingleOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

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

    private IQueryable<Order> Load() => db.Orders.Include(o => o.Items).Include(o => o.Timeline).AsSplitQuery();
}
