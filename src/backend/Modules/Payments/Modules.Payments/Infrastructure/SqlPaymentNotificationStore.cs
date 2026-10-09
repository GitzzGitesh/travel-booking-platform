using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.Modules.Payments.Application;

namespace TravelBooking.Modules.Payments.Infrastructure;

internal sealed class SqlPaymentNotificationStore(PaymentsDbContext db) : IPaymentNotificationStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public async Task<bool> TryAddAsync(PaymentNotificationRecord notification, CancellationToken cancellationToken)
    {
        db.PaymentNotifications.Add(notification);
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

    public async Task<IReadOnlyList<PaymentNotificationRecord>> FindUnprocessedAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.PaymentNotifications
            .Where(n => n.ProcessedAt == null && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
            .OrderBy(n => n.ReceivedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<Guid?> FindAttemptAsync(string providerId, string? reference, string? providerPaymentId, CancellationToken cancellationToken)
    {
        // Our reference is the attempt id ("N" format). It is only a hint: the lookup re-checks it with the provider.
        if (reference is not null && Guid.TryParseExact(reference, "N", out var attemptId)
            && await db.PaymentAttempts.AnyAsync(a => a.Id == attemptId, cancellationToken))
        {
            return attemptId;
        }

        if (providerPaymentId is null)
        {
            return null;
        }

        return await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.ProviderPaymentId == providerPaymentId)
            .Select(a => (Guid?)a.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
