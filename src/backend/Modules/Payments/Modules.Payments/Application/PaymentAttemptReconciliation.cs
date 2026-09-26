using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>
/// Brings one attempt up to date in the background (payment-lifecycle.md): an open attempt is looked up by our
/// reference, never authorized again; a hold Orders will not use is released with one void, keyed by the attempt so it
/// never releases twice; a void whose outcome is unknown, or that a crash interrupted, is looked up and repeated with the
/// same key only while the payment is still held. Nothing is ever captured here.
/// </summary>
internal sealed class PaymentAttemptReconciler(
    IPaymentAttemptStore store,
    AuthorizeOrderPaymentHandler payments,
    PaymentOperations operations,
    TimeProvider timeProvider)
{
    public const string Actor = "system:payment-reconciliation";

    public async Task ReconcileAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(attemptId, cancellationToken) is not { } attempt)
        {
            return;
        }

        if (!attempt.IsAuthorizationSettled)
        {
            (attempt, _) = await payments.LookUpAsync(attempt, Actor, null, cancellationToken);
        }

        if (attempt.IsVoidInProgress)
        {
            await ResumeVoidAsync(attempt, cancellationToken);
        }
        else if (attempt.ReleaseRequestedAt is not null && attempt.Status is PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired)
        {
            await VoidAsync(attempt, cancellationToken);
        }
    }

    private async Task VoidAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        // Saved as Voiding before the provider is asked: a crash leaves a void to look up, never a forgotten one.
        if (!attempt.BeginVoid(Change()).IsSuccess || !await store.TrySaveAsync(cancellationToken))
        {
            return;
        }

        await SendVoidAsync(attempt, cancellationToken);
    }

    /// <summary>A void that was interrupted or whose outcome is unknown: look it up; repeat it (same key) only if still held.</summary>
    private async Task ResumeVoidAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), cancellationToken);
        switch (outcome)
        {
            case PaymentOutcome.Voided:
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.Voided, "Found voided at the provider", cancellationToken);
                break;
            case PaymentOutcome.Canceled:
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.Canceled, "Found canceled at the provider", cancellationToken);
                break;
            case PaymentOutcome.Authorized or PaymentOutcome.ActionRequired:
                await SendVoidAsync(attempt, cancellationToken);
                break;
            case PaymentOutcome.Unknown or PaymentOutcome.Rejected:
                break; // the lookup itself failed: try again next run
            default:
                // Not found, captured, or something else: a hold we had is not what we expected.
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.ManualReview, $"While releasing, the provider reported {outcome.GetType().Name}", cancellationToken);
                break;
        }
    }

    private async Task SendVoidAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var details = new VoidDetails(
            new PaymentReference(attempt.Reference),
            new ProviderPaymentRef(attempt.ProviderId!, attempt.ProviderPaymentId!),
            new OperationKey(attempt.VoidKey));
        var (status, reason) = await operations.VoidAsync(details, cancellationToken) switch
        {
            PaymentOutcome.Voided => (PaymentAttemptStatus.Voided, "Hold released (voided)"),
            PaymentOutcome.Canceled => (PaymentAttemptStatus.Canceled, "Unfinished challenge canceled; nothing was held"),
            PaymentOutcome.Unknown unknown => (PaymentAttemptStatus.VoidUnknown, $"Void outcome unknown ({unknown.Cause}); to be looked up"),
            PaymentOutcome.Rejected rejected => (PaymentAttemptStatus.ManualReview, $"Void refused ({rejected.Reason})"),
            var other => (PaymentAttemptStatus.ManualReview, $"While releasing, the provider reported {other.GetType().Name}"),
        };
        await ResolveVoidAsync(attempt, status, reason, cancellationToken);
    }

    private async Task ResolveVoidAsync(PaymentAttempt attempt, PaymentAttemptStatus status, string reason, CancellationToken cancellationToken)
    {
        // A lost save is harmless: the attempt stays voiding, and the next run looks the void up.
        if (attempt.ResolveVoid(status, reason, Change()).IsSuccess)
        {
            await store.TrySaveAsync(cancellationToken);
        }
    }

    private PaymentChange Change() => new(timeProvider.GetUtcNow(), Actor, null);
}

/// <summary>The reconciliation job: each attempt on the work list in its own scope, so one failure never blocks the rest.</summary>
internal sealed partial class ReconcilePaymentAttemptsJob(
    IPaymentAttemptStore store,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<PaymentReconciliationOptions> options,
    ILogger<ReconcilePaymentAttemptsJob> logger) : IBackgroundJob
{
    public const string Name = "payments.reconcile-attempts";
    public const int BatchSize = 50;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        var work = await store.FindReconcilableAsync(timeProvider.GetUtcNow() - options.Value.LookupAfter, BatchSize, cancellationToken);
        foreach (var attemptId in work)
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break; // the rest waits for the next run, well inside the lease
            }

            handled++;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PaymentAttemptReconciler>().ReconcileAsync(attemptId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, attemptId, exception.GetType().Name);
            }
        }

        return handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling payment attempt {AttemptId} failed ({Error}); it stays on the work list")]
    private static partial void LogFailed(ILogger logger, Guid attemptId, string error);
}

/// <summary>
/// Records Orders' request to release a hold (ADR 0007 inbox: once per event), for the reconciliation job to act on
/// once the payment's outcome is known. It never calls the provider itself.
/// </summary>
internal sealed class OrderPaymentReleaseRequestedHandler(IPaymentAttemptStore store, TimeProvider timeProvider)
    : IIntegrationEventHandler<OrderPaymentReleaseRequested>
{
    public const string Name = "payments.order-payment-release-requested";
    public const string Actor = "system:orders";

    public async Task HandleAsync(OrderPaymentReleaseRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (await store.FindAsync(integrationEvent.PaymentId, cancellationToken) is { } attempt && attempt.OrderId == integrationEvent.OrderId)
        {
            attempt.RequestRelease(integrationEvent.Reason, new PaymentChange(now, Actor, integrationEvent.CorrelationId));
        }

        store.MarkConsumed(integrationEvent.EventId, Name, now);
        if (!await store.TrySaveAsync(cancellationToken) && !await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            // The attempt changed at the same moment: fail so the outbox delivers the event again.
            throw new InvalidOperationException($"Payment attempt {integrationEvent.PaymentId} changed concurrently.");
        }
    }
}
