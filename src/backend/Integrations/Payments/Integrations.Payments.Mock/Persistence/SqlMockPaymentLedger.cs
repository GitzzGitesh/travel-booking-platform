using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock.Persistence;

/// <summary>
/// The mock's payments in SQL (ADR 0032), so the Api and the Worker (two processes) see the same payments: what the Api
/// authorized, the Worker can capture, void or refund. Each call is one transaction that first takes an exclusive
/// application lock on its payment reference, so calls for one payment run one at a time across processes, as a real
/// provider serialises them; calls for different payments never wait on each other.
/// </summary>
/// <remarks>
/// Operation keys are scoped by the lock of the payment they are used on. One key used at the same moment on two
/// different payments (never done by the Payments module, whose keys name their attempt) meets the key's primary key
/// instead: the later call rolls back and is reported unavailable, never applied twice.
/// </remarks>
internal sealed partial class SqlMockPaymentLedger(
    IServiceScopeFactory scopes, IOptions<MockPaymentProviderOptions> options, ILogger<SqlMockPaymentLedger> logger) : IMockPaymentLedger
{

    public async Task<T> RunAsync<T>(PaymentReference reference, OperationKey? key, Func<MockLedgerEntry, T> operation, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope(); // the call's own context, disposed with the scope
        IDbContextTransaction? transaction = null;
        try
        {
            T result;
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<MockPaymentsDbContext>();
                transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                result = await ApplyAsync(db, reference, key, operation, options.Value.LockTimeoutMilliseconds, cancellationToken);
            }
            catch (Exception failure) when (failure is DbException or DbUpdateException or InvalidOperationException)
            {
                // Nothing is committed before the commit below: the transaction rolls back when it is disposed. This
                // includes a context that cannot be made (no connection string).
                LogLedgerFailure(logger, reference.Value, ProviderErrorKind.Unavailable, failure.GetType().Name, SqlNumber(failure));
                throw new MockLedgerException(ProviderErrorKind.Unavailable, "The mock payment state could not be read or kept; nothing was done.", failure);
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception failure) when (failure is DbException or InvalidOperationException)
            {
                // The commit may have reached the database before the failure: the outcome is unknown, so the caller
                // reconciles by our reference instead of resubmitting (booking-and-payments rules).
                LogLedgerFailure(logger, reference.Value, ProviderErrorKind.Unknown, failure.GetType().Name, SqlNumber(failure));
                throw new MockLedgerException(ProviderErrorKind.Unknown, "The mock payment state may or may not have been kept.", failure);
            }

            return result;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    // Under the payment's lock: read what the call can see, run it, and stage what it changed (saved, not committed).
    private static async Task<T> ApplyAsync<T>(
        MockPaymentsDbContext db, PaymentReference reference, OperationKey? key, Func<MockLedgerEntry, T> operation, int lockTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DECLARE @granted int;
            EXEC @granted = sp_getapplock @Resource = {"paymentsmock:" + reference.Value}, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = {lockTimeoutMilliseconds};
            IF @granted < 0 THROW 50001, 'The mock payment is busy.', 1;
            """,
            cancellationToken);

        var keyValue = key?.Value;
        var paymentRow = await db.Payments.SingleOrDefaultAsync(p => p.Reference == reference.Value, cancellationToken);
        var operationRow = keyValue is null ? null : await db.Operations.SingleOrDefaultAsync(o => o.Key == keyValue, cancellationToken);
        var refundRow = keyValue is null ? null : await db.Refunds.SingleOrDefaultAsync(r => r.Key == keyValue, cancellationToken);
        var failedOnce = keyValue is not null && await db.FailedOnce.AnyAsync(f => f.Key == keyValue, cancellationToken);

        var entry = new MockLedgerEntry
        {
            Payment = paymentRow?.ToPayment(),
            Operation = operationRow?.Operation,
            Refund = refundRow?.ToRefund(),
            FailedOnce = failedOnce,
        };

        var result = operation(entry);

        if (entry.Payment is { } payment)
        {
            if (paymentRow is null)
            {
                db.Payments.Add(MockPaymentRow.From(payment));
            }
            else
            {
                paymentRow.Update(payment);
            }
        }

        if (keyValue is not null)
        {
            if (entry.Operation is { } what && operationRow is null)
            {
                db.Operations.Add(new MockOperationRow { Key = keyValue, Operation = what });
            }

            if (entry.Refund is { } refund && refundRow is null)
            {
                db.Refunds.Add(MockRefundRow.From(refund));
            }

            if (entry.FailedOnce && !failedOnce)
            {
                db.FailedOnce.Add(new MockFailedOnceRow { Key = keyValue });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    // The SQL Server error number, when there is one: enough to tell a missing schema (208) from a busy payment (50001)
    // or an unreachable server, without the message text, which can name the server.
    private static int? SqlNumber(Exception failure) =>
        (failure as Microsoft.Data.SqlClient.SqlException ?? failure.InnerException as Microsoft.Data.SqlClient.SqlException)?.Number;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mock payment state unusable for payment {PaymentReference} ({Outcome}): {ExceptionType}, SQL error {SqlNumber}.")]
    private static partial void LogLedgerFailure(ILogger logger, string paymentReference, ProviderErrorKind outcome, string exceptionType, int? sqlNumber);
}
