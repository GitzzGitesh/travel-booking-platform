using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>
/// Records Orders' approved refund (ADR 0027; inbox: once per event, and once per refund id). It is checked against what
/// can still be refunded (captured, less the refunds that have not failed): a refund that would exceed it, or for a
/// payment that was not captured, is recorded as failed and reported, never sent. It never calls the provider itself.
/// </summary>
internal sealed partial class OrderRefundRequestedHandler(IPaymentAttemptStore store, TimeProvider timeProvider, ILogger<OrderRefundRequestedHandler> logger)
    : IIntegrationEventHandler<OrderRefundRequested>
{
    public const string Name = "payments.order-refund-requested";

    public async Task HandleAsync(OrderRefundRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken)
            || await store.FindRefundAsync(integrationEvent.RefundId, cancellationToken) is not null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var reason = await NotPossibleBecauseAsync(integrationEvent, now, cancellationToken);
        if (reason is null)
        {
            store.AddRefund(RefundRecord.Requested(integrationEvent.RefundId, integrationEvent.PaymentId, integrationEvent.OrderId, integrationEvent.Amount,
                integrationEvent.CorrelationId, now));
        }
        else
        {
            LogNotPossible(logger, integrationEvent.RefundId, integrationEvent.OrderId, reason);
            store.AddRefund(RefundRecord.NotPossible(integrationEvent.RefundId, integrationEvent.PaymentId, integrationEvent.OrderId, integrationEvent.Amount,
                reason, integrationEvent.CorrelationId, now));
            store.Publish(new PaymentRefundSettled(Guid.NewGuid(), now, integrationEvent.OrderId, integrationEvent.PaymentId, integrationEvent.RefundId,
                integrationEvent.Amount, Succeeded: false, integrationEvent.CorrelationId), integrationEvent.CorrelationId);
        }

        store.MarkConsumed(integrationEvent.EventId, Name, now);
        if (!await store.TrySaveAsync(cancellationToken) && !await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            throw new InvalidOperationException($"Refund {integrationEvent.RefundId} could not be recorded; it is delivered again.");
        }
    }

    // Reserved on the attempt in the same save (its rowversion serialises refunds of one payment), never check-then-insert.
    private async Task<string?> NotPossibleBecauseAsync(OrderRefundRequested request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(request.PaymentId, cancellationToken) is not { } attempt || attempt.OrderId != request.OrderId)
        {
            return "no such payment for this order";
        }

        if (attempt.Status is not PaymentAttemptStatus.Captured || attempt.CaptureAmount is not { } captured)
        {
            return $"the payment is not captured ({attempt.Status})";
        }

        if (request.Amount.Currency != captured.Currency || request.Amount.Amount <= 0)
        {
            return "the amount is not in the captured currency, or not positive";
        }

        return attempt.ReserveRefund(request.Amount, request.RefundId, new PaymentChange(now, "system:orders", request.CorrelationId))
            ? null
            : "it would exceed what can still be refunded";
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "RefundNotPossible",
        Message = "Alert: refund {RefundId} for order {OrderId} was not sent: {Reason}")]
    private static partial void LogNotPossible(ILogger logger, Guid refundId, Guid orderId, string reason);
}

/// <summary>
/// The Worker's refunds (ADR 0006, ADR 0027): a requested refund is saved as Refunding, then sent once under our key; an
/// outcome that is pending, unknown or interrupted is looked up by that key and never sent again. Still unknown after
/// <see cref="RefundRecord.UnresolvedAfter"/>, it goes to a person (<c>RefundUnresolved</c>). A final outcome is
/// published to Orders (and the customer's notice) in the same save.
/// </summary>
internal sealed partial class ExecuteRefundsJob(
    IPaymentAttemptStore store, IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<ExecuteRefundsJob> logger) : IBackgroundJob
{
    public const string Name = "payments.execute-refunds";
    public const string Actor = "system:refunds";
    public const int BatchSize = 20;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        foreach (var id in await store.FindRefundsToProcessAsync(startedAt - RefundRecord.InterruptedAfter, BatchSize, cancellationToken))
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break;
            }

            // Each refund in its own scope: a concurrent change or a failure never leaks into the next.
            await using var scope = scopes.CreateAsyncScope();
            var refunds = scope.ServiceProvider.GetRequiredService<IPaymentAttemptStore>();
            var operations = scope.ServiceProvider.GetRequiredService<PaymentOperations>();
            if (await refunds.FindRefundAsync(id, cancellationToken) is { } refund && await refunds.FindAsync(refund.AttemptId, cancellationToken) is { } attempt)
            {
                await ProcessAsync(refunds, operations, refund, attempt, cancellationToken);
                handled++;
            }
        }

        return handled;
    }

    private async Task ProcessAsync(IPaymentAttemptStore refunds, PaymentOperations operations, RefundRecord refund, PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var reference = new PaymentReference(attempt.Reference);
        var key = new OperationKey(refund.Key);
        if (refund.Status is RefundRecordStatus.Requested)
        {
            if (attempt.ProviderId is null || attempt.ProviderPaymentId is null)
            {
                // Never sent without the provider's payment id; a person settles it (never silent).
                await RecordAsync(refunds, refund, attempt, new PaymentOutcome.Unknown(ProviderErrorKind.InvalidRequest), cancellationToken, "the payment has no provider id");
                return;
            }

            if (!refund.BeginRefund(timeProvider.GetUtcNow()))
            {
                return;
            }

            if (!await refunds.TrySaveAsync(cancellationToken))
            {
                return; // changed concurrently: the next run sees it as it is
            }

            var sent = await operations.RefundAsync(
                new RefundDetails(reference, new ProviderPaymentRef(attempt.ProviderId, attempt.ProviderPaymentId), key, refund.Amount), cancellationToken);
            await RecordAsync(refunds, refund, attempt, sent, cancellationToken);
            return;
        }

        // Pending, unknown or interrupted: only a lookup by our key, never a second refund.
        var found = await operations.ReconcileRefundAsync(reference, key, refund.Amount, cancellationToken);
        await RecordAsync(refunds, refund, attempt, found, cancellationToken);
    }

    private async Task RecordAsync(
        IPaymentAttemptStore refunds, RefundRecord refund, PaymentAttempt attempt, PaymentOutcome outcome, CancellationToken cancellationToken, string? cause = null)
    {
        var now = timeProvider.GetUtcNow();
        var unresolved = now - refund.RequestedAt >= RefundRecord.UnresolvedAfter;
        var (status, reason, providerRefundId) = outcome switch
        {
            _ when cause is not null => (RefundRecordStatus.ManualReview, $"Not sent: {cause}", null),
            PaymentOutcome.RefundSucceeded succeeded => (RefundRecordStatus.Succeeded, "Refunded", succeeded.Refund.Refund.Value),
            PaymentOutcome.RefundFailed failed => (RefundRecordStatus.Failed, "The provider could not return the money", failed.Refund.Refund.Value),
            // Still pending after its limit goes to a person too (ADR 0027 §6): never forgotten at the provider.
            PaymentOutcome.RefundPending when unresolved => (RefundRecordStatus.ManualReview, $"Still pending at the provider after {RefundRecord.UnresolvedAfter.TotalHours:0} hours", null),
            PaymentOutcome.RefundPending pending => (RefundRecordStatus.Pending, "Accepted by the provider; final outcome to be looked up", pending.Refund.Refund.Value),
            // Only an invalid request proves nothing was refunded; a key used for other details needs a person.
            PaymentOutcome.Rejected { Reason: ProviderErrorKind.InvalidRequest } when refund.Status is RefundRecordStatus.Refunding =>
                (RefundRecordStatus.Failed, "Refused by the provider before processing (invalid request)", null),
            PaymentOutcome.Rejected rejected when refund.Status is RefundRecordStatus.Refunding =>
                (RefundRecordStatus.ManualReview, $"Refused by the provider ({rejected.Reason}); a person checks it", null),
            PaymentOutcome.Mismatch => (RefundRecordStatus.ManualReview, "The provider holds a refund under our key that is not as expected", null),
            _ when unresolved => (RefundRecordStatus.ManualReview, $"Outcome still unknown after {RefundRecord.UnresolvedAfter.TotalHours:0} hours", null),
            PaymentOutcome.Unknown unknown => (RefundRecordStatus.Unknown, $"Outcome unknown ({unknown.Cause}); to be looked up", null),
            _ => (RefundRecordStatus.Unknown, $"The provider reported {outcome.GetType().Name}; to be looked up", null),
        };

        if (status is RefundRecordStatus.ManualReview && refund.Status is not RefundRecordStatus.ManualReview)
        {
            LogUnresolved(logger, refund.Id, refund.OrderId, reason);
        }

        var previous = refund.Status;
        var change = new PaymentChange(now, Actor, refund.CorrelationId);
        if (refund.Record(status, reason, now, providerRefundId))
        {
            if (status is RefundRecordStatus.Failed)
            {
                LogFailed(logger, refund.Id, refund.OrderId, reason);
                attempt.ReleaseRefund(refund.Amount, refund.Id, change); // nothing was refunded: it can be refunded again
            }
            else
            {
                attempt.RecordFinding($"Refund {refund.Id:N} of {refund.AmountValue} {refund.CurrencyCode} succeeded", change, providerRefundId);
            }

            refunds.Publish(new PaymentRefundSettled(Guid.NewGuid(), now, refund.OrderId, refund.AttemptId, refund.Id, refund.Amount,
                status is RefundRecordStatus.Succeeded, refund.CorrelationId), refund.CorrelationId);
        }

        else if (refund.Status != previous)
        {
            attempt.RecordFinding($"Refund {refund.Id:N}: {refund.Status} ({reason})", change, providerRefundId); // the refund's history
        }

        // A lost save is harmless: the refund stays as it was, and the next run looks it up by our key.
        await refunds.TrySaveAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "RefundUnresolved",
        Message = "Alert: refund {RefundId} for order {OrderId} needs a person: {Reason}")]
    private static partial void LogUnresolved(ILogger logger, Guid refundId, Guid orderId, string reason);

    [LoggerMessage(Level = LogLevel.Error, EventName = "RefundFailed",
        Message = "Alert: refund {RefundId} for order {OrderId} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid refundId, Guid orderId, string reason);
}
