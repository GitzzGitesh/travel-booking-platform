using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.Infrastructure;

internal sealed class SqlPaymentAttemptStore(PaymentsDbContext db) : IPaymentAttemptStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

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
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
