using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>Asking Payments to release a hold the order will not use: a timeline note and an outbox event, saved together.</summary>
internal static class PaymentHolds
{
    /// <summary>
    /// Notes the unused hold and publishes the release request, once per payment (the note is the guard). Returns false
    /// when the note was already there.
    /// </summary>
    public static bool RequestRelease(IOrderStore store, Order order, Guid paymentId, string reason, TransitionContext context)
    {
        if (!order.NoteUnusedPaymentHold(paymentId.ToString(), reason, context))
        {
            return false;
        }

        Publish(store, order.Id, paymentId, reason, context);
        return true;
    }

    /// <summary>Publishes a (new) release request event. Payments' inbox makes a repeated request harmless.</summary>
    public static void Publish(IOrderStore store, Guid orderId, Guid paymentId, string reason, TransitionContext context) =>
        store.Publish(new OrderPaymentReleaseRequested(Guid.NewGuid(), context.At, orderId, paymentId, reason, context.CorrelationId), context.CorrelationId);
}

/// <summary>
/// One order whose offer expired before payment (F-02). It is abandoned only when no payment attempt for it still holds,
/// or may hold, funds (booking rules: never abandoned while a hold may exist):
/// - no live attempt: the expired items are abandoned;
/// - an authorized hold or an unfinished challenge: Payments is asked to release it, and the order waits;
/// - an outcome or a void still unknown, or a manual review: the order waits for Payments to settle it.
/// Every step is idempotent, so a repeat, or a second Worker after a crash, changes nothing more.
/// </summary>
internal sealed class ExpireUnpaidOrderHandler(IOrderStore store, IOrderPayments payments, TimeProvider timeProvider)
{
    public const string Actor = "system:order-expiry";
    public const string Reason = "the offer expired before booking";

    public async Task<ExpiryOutcome> HandleAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var context = new TransitionContext(timeProvider.GetUtcNow(), Actor);
        if (await store.FindAsync(orderId, cancellationToken) is not { } order
            || !order.Items.Any(i => i.Status is FlightOrderItemStatus.AwaitingPayment && i.OfferExpiresAt <= context.At))
        {
            return ExpiryOutcome.NothingToDo;
        }

        var live = await payments.FindLiveAsync(orderId, cancellationToken);
        var outcome = live switch
        {
            null => order.AbandonExpired(context) > 0 ? ExpiryOutcome.Abandoned : ExpiryOutcome.NothingToDo,
            { Status: OrderPaymentStatus.Authorized or OrderPaymentStatus.ActionRequired, ReleaseRequested: false }
                => await RequestReleaseAsync(order, live.PaymentId, context, cancellationToken),
            _ => ExpiryOutcome.WaitingForPayment, // unknown, releasing, or with a person
        };

        return outcome is ExpiryOutcome.NothingToDo or ExpiryOutcome.WaitingForPayment || await store.TrySaveAsync(cancellationToken)
            ? outcome
            : ExpiryOutcome.Conflict;
    }

    private async Task<ExpiryOutcome> RequestReleaseAsync(Order order, Guid paymentId, TransitionContext context, CancellationToken cancellationToken)
    {
        // Already asked, yet Payments has not recorded it: if that request is no longer on its way (dispatch gave up after
        // an outage), ask again rather than leave the customer's funds held.
        if (!PaymentHolds.RequestRelease(store, order, paymentId, Reason, context)
            && !await store.IsReleaseRequestPendingAsync(paymentId, cancellationToken))
        {
            PaymentHolds.Publish(store, order.Id, paymentId, Reason, context);
        }

        return ExpiryOutcome.ReleaseRequested;
    }
}

internal enum ExpiryOutcome
{
    NothingToDo,
    Abandoned,
    ReleaseRequested,
    WaitingForPayment,

    /// <summary>The order changed at the same time: it stays on the work list for the next run.</summary>
    Conflict,
}

/// <summary>The offer-expiry job: each order on the work list in its own scope, so one failure never blocks the rest.</summary>
internal sealed partial class ExpireUnpaidOrdersJob(
    IOrderStore store,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<ExpireUnpaidOrdersJob> logger) : IBackgroundJob
{
    public const string Name = "orders.expire-unpaid";

    // Orders waiting for Payments stay on the list and are cheap to skip; a large batch keeps them from starving the rest.
    public const int BatchSize = 200;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        var work = await store.FindWithExpiredUnpaidItemsAsync(timeProvider.GetUtcNow(), BatchSize, cancellationToken);
        foreach (var orderId in work)
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break; // the rest waits for the next run, well inside the lease
            }

            handled++;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExpireUnpaidOrderHandler>().HandleAsync(orderId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, orderId, exception.GetType().Name);
            }
        }

        return handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expiring order {OrderId} failed ({Error}); it stays on the work list")]
    private static partial void LogFailed(ILogger logger, Guid orderId, string error);
}
