using System.Linq.Expressions;
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

    /// <summary>Loads tried before giving up when the order keeps changing underneath (see <see cref="LoadAsync"/>).</summary>
    internal const int MaxLoadAttempts = 5;

    public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        LoadAsync(o => o.Id == orderId, tracked: true, cancellationToken);

    public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
        LoadAsync(o => o.Id == orderId && o.CustomerId == customerId, tracked: true, cancellationToken);

    public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        LoadAsync(o => o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey, tracked: false, cancellationToken);

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
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            // Forget this unit of work (its changes and outbox rows), so the next read sees the order as stored, not as
            // this request had changed it. A unique violation (e.g. a refund case's idempotency key) is a concurrent repeat.
            db.ChangeTracker.Clear();
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
        await db.Set<OrderItem>().AsNoTracking()
            .Where(i => i.Status == OrderItemStatus.AwaitingPayment && i.OfferExpiresAt <= now)
            .GroupBy(i => EF.Property<Guid>(i, "OrderId"))
            .OrderBy(g => g.Min(i => i.OfferExpiresAt))
            .Select(g => g.Key)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> FindBookingsToReconcileAsync(DateTimeOffset startedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Set<OrderItem>().AsNoTracking()
            .Where(i => (i.Status == OrderItemStatus.PendingConfirmation
                    || (i.Status == OrderItemStatus.Booking && (i.BookingStartedAt == null || i.BookingStartedAt <= startedBefore)))
                && (i.NextBookingLookupAt == null || i.NextBookingLookupAt <= now))
            .GroupBy(i => EF.Property<Guid>(i, "OrderId"))
            .OrderBy(g => g.Min(i => i.NextBookingLookupAt ?? i.BookingStartedAt))
            .Select(g => g.Key)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> FindWithItemStatusAsync(
        OrderItemStatus status, (DateTimeOffset CreatedAt, Guid Id)? after, int limit, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.Items.Any(i => i.Status == status))
            .Where(o => after == null || o.CreatedAt > after.Value.CreatedAt || (o.CreatedAt == after.Value.CreatedAt && o.Id.CompareTo(after.Value.Id) > 0))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    // The reference compares as the column's collation does (case-insensitive): staff type it as they read it.
    public async Task<IReadOnlyList<Order>> SearchAsync(string? bookingReference, Guid? orderId, int limit, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => (orderId != null && o.Id == orderId) || (bookingReference != null && o.Items.Any(i => i.SupplierLocator == bookingReference)))
            .OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Audit(BuildingBlocks.Audit.AuditEntry entry) => db.Set<BuildingBlocks.Audit.AuditEntry>().Add(entry);

    // The order, its items and its timeline are read by separate queries (split query). A change committed between them
    // would give a torn aggregate: for instance items already in Booking on an order without its payment authorization,
    // which a duplicate checkout then refuses as not payable. Every change rewrites the order row (Order.Revision), so a
    // load counts only when the order's rowversion is the same before and after it; otherwise it is repeated. A commit
    // after that is caught by the rowversion check when saving.
    // Detaches this order's own torn copy (the order, its items with their owned values, and its timeline) before reading
    // it again: never the rest of the unit of work, so a case or request the caller loaded before the order stays tracked
    // and its change is saved.
    private void Forget(Guid orderId)
    {
        var entries = db.ChangeTracker.Entries().ToList();
        var graph = entries.Where(e => e.Entity switch
        {
            Order order => order.Id == orderId,
            OrderItem => e.Property("OrderId").CurrentValue is Guid owner && owner == orderId,
            OrderTimelineEntry timeline => timeline.OrderId == orderId,
            _ => false,
        }).ToList();
        var itemIds = graph.Select(e => e.Entity).OfType<OrderItem>().Select(i => i.Id).ToHashSet();
        var owned = entries.Where(e => e.Metadata.IsOwned() && e.Metadata.FindOwnership() is { } ownership
            && ownership.Properties.Any(p => e.Property(p.Name).CurrentValue is Guid owner && itemIds.Contains(owner)));
        foreach (var entry in owned.Concat(graph).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<Order?> LoadAsync(Expression<Func<Order, bool>> predicate, bool tracked, CancellationToken cancellationToken)
    {
        // A repeated tracked load detaches this order's torn copy, so the unit of work must not hold anything unsaved
        // (IOrderStore remarks): changes made before the load could otherwise be lost with it.
        if (tracked && db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException("Load the order before changing anything in this unit of work.");
        }

        for (var attempt = 1; ; attempt++)
        {
            if (await db.Orders.AsNoTracking().Where(predicate).Select(o => new { o.Id, RowVersion = EF.Property<byte[]>(o, "RowVersion") })
                    .SingleOrDefaultAsync(cancellationToken) is not { } before)
            {
                return null;
            }

            var query = db.Orders.Where(predicate).Include(o => o.Items).Include(o => o.Timeline).AsSplitQuery();
            var order = await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken);
            if (order is not null && await RowVersion(order.Id, cancellationToken) is { } after && after.AsSpan().SequenceEqual(before.RowVersion))
            {
                return order;
            }

            if (tracked)
            {
                Forget(before.Id);
            }

            if (attempt == MaxLoadAttempts)
            {
                throw new OrderKeptChangingException(before.Id);
            }
        }
    }

    private Task<byte[]?> RowVersion(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => EF.Property<byte[]?>(o, "RowVersion")).SingleOrDefaultAsync(cancellationToken);
}

internal sealed class SqlRefundCaseStore(OrdersDbContext db) : IRefundCaseStore
{
    public void Add(RefundCase refundCase) => db.Set<RefundCase>().Add(refundCase);

    public Task<RefundCase?> FindAsync(Guid caseId, CancellationToken cancellationToken) =>
        db.Set<RefundCase>().SingleOrDefaultAsync(r => r.Id == caseId, cancellationToken);

    public Task<RefundCase?> PeekAsync(Guid caseId, CancellationToken cancellationToken) =>
        db.Set<RefundCase>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == caseId, cancellationToken);

    public async Task<IReadOnlyList<RefundCase>> FindForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<RefundCase>().AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.RequestedAt).ToListAsync(cancellationToken);

    public Task<RefundCase?> FindByKeyAsync(string staffId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.Set<RefundCase>().AsNoTracking().SingleOrDefaultAsync(r => r.RequestedBy == staffId && r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<RefundCase>> FindOverdueAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken) =>
        await db.Set<RefundCase>()
            .Where(r => r.Status == RefundCaseStatus.Approved && r.DecidedAt <= decidedBefore && r.OverdueAlertedAt == null)
            .OrderBy(r => r.DecidedAt).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefundCase>> FindPendingAsync(int limit, CancellationToken cancellationToken) =>
        await db.Set<RefundCase>().AsNoTracking().Where(r => r.Status == RefundCaseStatus.PendingApproval)
            .OrderBy(r => r.RequestedAt).Take(limit).ToListAsync(cancellationToken);

    public Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken) =>
        db.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId && m.Handler == handler, cancellationToken);

    public void MarkConsumed(Guid messageId, string handler, DateTimeOffset at) =>
        db.Set<InboxMessage>().Add(InboxMessage.For(messageId, handler, at));
}

internal sealed class SqlCancellationRequestStore(OrdersDbContext db) : ICancellationRequestStore
{
    public void Add(CancellationRequest request) => db.Set<CancellationRequest>().Add(request);

    public Task<CancellationRequest?> FindAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.Set<CancellationRequest>().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public Task<CancellationRequest?> PeekAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.Set<CancellationRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public Task<CancellationRequest?> FindByKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.Set<CancellationRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.CustomerId == customerId && r.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<CancellationRequest?> FindOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Set<CancellationRequest>().SingleOrDefaultAsync(r => r.OrderId == orderId && r.Status == CancellationRequestStatus.Open, cancellationToken);

    public Task<CancellationRequest?> FindLatestForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Set<CancellationRequest>().AsNoTracking().Where(r => r.OrderId == orderId)
            .OrderByDescending(r => r.Status == CancellationRequestStatus.Open) // the open one (at most one) first, whatever the clock
            .ThenByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CancellationRequest>> FindOpenAsync(int limit, CancellationToken cancellationToken) =>
        await db.Set<CancellationRequest>().AsNoTracking().Where(r => r.Status == CancellationRequestStatus.Open)
            .OrderBy(r => r.RequestedAt).Take(limit).ToListAsync(cancellationToken);
}

internal sealed class SqlOrderHistory(OrdersDbContext db) : IOrderHistory
{
    public async Task<IReadOnlyList<Order>> FindForCustomerAsync(
        string customerId, (DateTimeOffset CreatedAt, Guid Id)? before, int limit, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.CustomerId == customerId)
            .Where(o => before == null || o.CreatedAt < before.Value.CreatedAt || (o.CreatedAt == before.Value.CreatedAt && o.Id.CompareTo(before.Value.Id) < 0))
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
}
