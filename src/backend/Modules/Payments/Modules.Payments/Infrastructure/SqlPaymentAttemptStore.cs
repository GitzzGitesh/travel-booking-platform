using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.Infrastructure;

internal sealed class SqlPaymentAttemptStore(PaymentsDbContext db) : IPaymentAttemptStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    private static readonly PaymentAttemptStatus[] _lookupStatuses =
    [
        PaymentAttemptStatus.Authorizing, PaymentAttemptStatus.AuthorizationUnknown, PaymentAttemptStatus.ActionRequired,
        PaymentAttemptStatus.Voiding, PaymentAttemptStatus.VoidUnknown,
    ];

    public Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken) =>
        db.PaymentAttempts.Include(a => a.Events).SingleOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

    public Task<PaymentAttempt?> FindByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.PaymentAttempts.Include(a => a.Events).SingleOrDefaultAsync(a => a.OrderId == orderId && a.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> TryAddAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        db.PaymentAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
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
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || IsUniqueViolation(exception))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task<PaymentAttempt?> FindLiveByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.PaymentAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OrderId == orderId && PaymentAttempt.LiveStatuses.Contains(a.Status), cancellationToken);

    public async Task<IReadOnlyList<Guid>> FindReconcilableAsync(DateTimeOffset settledBefore, int limit, CancellationToken cancellationToken) =>
        await db.PaymentAttempts.AsNoTracking()
            .Where(a => (_lookupStatuses.Contains(a.Status) && a.UpdatedAt <= settledBefore)
                || (a.Status == PaymentAttemptStatus.Authorized && a.ReleaseRequestedAt != null))
            .OrderBy(a => a.UpdatedAt)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> CountForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.PaymentAttempts.CountAsync(a => a.OrderId == orderId, cancellationToken);

    public Task<int> CountForCustomerSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.PaymentAttempts.CountAsync(a => a.CustomerId == customerId && a.CreatedAt >= since, cancellationToken);

    public void RecordLimitTrip(AttemptLimitTrip trip) => db.Set<AttemptLimitTrip>().Add(trip);

    public Task<int> CountLimitTripsSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.Set<AttemptLimitTrip>().CountAsync(t => t.CustomerId == customerId && t.At >= since, cancellationToken);

    public async Task<IReadOnlyList<CustomerTrips>> FindCustomersToReviewAsync(DateTimeOffset since, int minimumTrips, CancellationToken cancellationToken) =>
        await db.Set<AttemptLimitTrip>().AsNoTracking()
            .Where(t => t.At >= since)
            .GroupBy(t => t.CustomerId)
            .Where(g => g.Count() >= minimumTrips)
            .OrderByDescending(g => g.Count())
            .Select(g => new CustomerTrips(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

    public Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken) =>
        db.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId && m.Handler == handler, cancellationToken);

    public void MarkConsumed(Guid messageId, string handler, DateTimeOffset at) =>
        db.Set<InboxMessage>().Add(InboxMessage.For(messageId, handler, at));

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number);
}
