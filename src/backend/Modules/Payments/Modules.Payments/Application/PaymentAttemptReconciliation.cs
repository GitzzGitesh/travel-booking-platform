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
/// same key only while the payment is still held. A capture Orders requested for its confirmed bookings is made once,
/// keyed by the attempt; one whose outcome is unknown is looked up, and repeated with the same key only while the hold is
/// still there (the provider guarantees idempotency by key). Nothing is captured without Orders' request.
/// </summary>
internal sealed partial class PaymentAttemptReconciler(
    IPaymentAttemptStore store,
    AuthorizeOrderPaymentHandler payments,
    PaymentOperations operations,
    TimeProvider timeProvider,
    ILogger<PaymentAttemptReconciler> logger)
{
    public const string Actor = "system:payment-reconciliation";

    /// <summary>A capture whose outcome still cannot be looked up after this goes to a person, well before a hold lapses (about 7 days).</summary>
    internal static readonly TimeSpan CaptureUnresolvedAfter = TimeSpan.FromHours(24);

    /// <summary>Brings the attempt up to date; says what it did (for notification records and logs).</summary>
    public async Task<string> ReconcileAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(attemptId, cancellationToken) is not { } attempt)
        {
            return "No such attempt";
        }

        var lookedUp = !attempt.IsAuthorizationSettled;
        if (lookedUp)
        {
            (attempt, _) = await payments.LookUpAsync(attempt, Actor, null, cancellationToken);
        }

        var outcome = lookedUp ? $"Looked up: {attempt.Status}" : $"Authorization already settled ({attempt.Status}); no lookup";

        if (attempt.IsVoidInProgress)
        {
            await ResumeVoidAsync(attempt, cancellationToken);
            return $"{outcome}; void resumed";
        }

        if (attempt.IsCaptureInProgress)
        {
            await ResumeCaptureAsync(attempt, cancellationToken);
            return $"{outcome}; capture resumed";
        }

        if (attempt.CaptureRequestedAt is not null && attempt.Status is PaymentAttemptStatus.Authorized)
        {
            await CaptureAsync(attempt, cancellationToken);
            return $"{outcome}; captured";
        }

        if (attempt.ReleaseRequestedAt is not null && attempt.Status is PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired)
        {
            await VoidAsync(attempt, cancellationToken);
            return $"{outcome}; hold released";
        }

        return outcome;
    }

    /// <summary>
    /// A provider notification about this attempt: always a lookup, never the event's content (ADR 0006). An open attempt
    /// is reconciled as usual. A settled one is looked up too, because a notification may reveal what the attempt no
    /// longer expects: a hold on a payment we consider finished (e.g. a declined payment confirmed again, or one found
    /// after "not found" was concluded). Such a hold is released at once with its own key, recorded on the attempt's
    /// history, and alerted. <paramref name="hint"/> is the provider payment id the notification carried.
    /// </summary>
    public async Task<string> ReconcileNotifiedAsync(Guid attemptId, ProviderPaymentRef? hint, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(attemptId, cancellationToken) is not { } attempt)
        {
            return "No such attempt";
        }

        // A confirmed booking is being (or was) charged: its hold is expected, never a stray one to void. The capture is
        // resumed as usual; a captured attempt needs nothing.
        if (attempt.CaptureRequestedAt is not null)
        {
            return attempt.Status is PaymentAttemptStatus.Captured
                ? "Captured; nothing to do"
                : await ReconcileAsync(attempt.Id, cancellationToken);
        }

        if (!attempt.IsAuthorizationSettled || PaymentAttempt.LiveStatuses.Contains(attempt.Status))
        {
            if (!attempt.IsAuthorizationSettled && attempt.KnownProviderPayment() is null && hint is not null)
            {
                // The notification tells us the provider's id before our record does: a direct, consistent read.
                (attempt, _) = await payments.LookUpAsync(attempt, hint, Actor, null, cancellationToken);
            }

            return await ReconcileAsync(attempt.Id, cancellationToken);
        }

        var reference = new PaymentReference(attempt.Reference);
        var outcome = await operations.ReconcileAsync(reference, attempt.KnownProviderPayment() ?? hint, cancellationToken);
        if (outcome is not (PaymentOutcome.Authorized or PaymentOutcome.ActionRequired))
        {
            return $"Settled ({attempt.Status}); the provider reports {outcome.GetType().Name}: nothing held";
        }

        // A hold (or a hold that can still be completed) on an attempt we will never use: release it now.
        var payment = outcome is PaymentOutcome.Authorized authorized ? authorized.Payment : ((PaymentOutcome.ActionRequired)outcome).Payment;
        LogStrayHold(logger, attempt.Id, payment.Payment.ProviderId);
        var released = await operations.VoidAsync(new VoidDetails(reference, payment.Payment, new OperationKey($"{attempt.Reference}:stray-void")), cancellationToken);
        var result = released is PaymentOutcome.Voided or PaymentOutcome.Canceled or PaymentOutcome.AuthorizationExpired
            ? "released"
            : $"NOT released ({released.GetType().Name}): manual action needed";
        attempt.RecordFinding($"Hold found on a {attempt.Status} attempt; {result}", Change(), payment.Payment.Value);
        await store.TrySaveAsync(cancellationToken);
        if (result != "released")
        {
            // Fails the notification, so it is retried; after its attempts it is given up with this alert logged.
            throw new InvalidOperationException($"A hold on settled payment attempt {attempt.Id} could not be released.");
        }

        return $"Hold found on a {attempt.Status} attempt and {result}";
    }

    private async Task CaptureAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        // Saved as Capturing before the provider is asked: a crash leaves a capture to look up, never a forgotten one.
        if (!attempt.BeginCapture(Change()).IsSuccess || !await store.TrySaveAsync(cancellationToken))
        {
            return; // changed concurrently: the next run looks again
        }

        await SendCaptureAsync(attempt, cancellationToken);
    }

    /// <summary>A capture that was interrupted or whose outcome is unknown: look it up; repeat it (same key) only while still held.</summary>
    private async Task ResumeCaptureAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        switch (await operations.ReconcileAsync(new PaymentReference(attempt.Reference), attempt.KnownProviderPayment(), cancellationToken))
        {
            case PaymentOutcome.Captured captured when captured.Payment.Captured == attempt.CaptureAmount:
                await ResolveCaptureAsync(attempt, PaymentAttemptStatus.Captured, "Found captured at the provider", cancellationToken);
                break;
            case PaymentOutcome.Authorized:
                await SendCaptureAsync(attempt, cancellationToken); // still only held: the same key never charges twice
                break;
            case PaymentOutcome.Unknown or PaymentOutcome.Rejected when timeProvider.GetUtcNow() - attempt.CaptureRequestedAt >= CaptureUnresolvedAfter:
                await ResolveCaptureAsync(attempt, PaymentAttemptStatus.ManualReview, "Capture still unknown after its limit; the hold must not lapse unnoticed", cancellationToken);
                break;
            case PaymentOutcome.Unknown or PaymentOutcome.Rejected:
                break; // the lookup itself failed: try again next run
            case var other:
                // Lapsed (F-24), voided, another amount, or not found: the booking exists but the charge is not as agreed.
                await ResolveCaptureAsync(attempt, PaymentAttemptStatus.ManualReview, $"While charging, the provider reported {other.GetType().Name}", cancellationToken);
                break;
        }
    }

    private async Task SendCaptureAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var details = new CaptureDetails(
            new PaymentReference(attempt.Reference), new ProviderPaymentRef(attempt.ProviderId!, attempt.ProviderPaymentId!), new OperationKey(attempt.CaptureKey), attempt.CaptureAmount!.Value);
        var (status, reason) = await operations.CaptureAsync(details, cancellationToken) switch
        {
            PaymentOutcome.Captured => (PaymentAttemptStatus.Captured, "Charged for the confirmed bookings (captured)"),
            PaymentOutcome.Unknown unknown => (PaymentAttemptStatus.CaptureUnknown, $"Capture outcome unknown ({unknown.Cause}); to be looked up"),
            PaymentOutcome.Rejected rejected => (PaymentAttemptStatus.ManualReview, $"Capture refused ({rejected.Reason})"),
            var other => (PaymentAttemptStatus.ManualReview, $"While charging, the provider reported {other.GetType().Name}"),
        };
        await ResolveCaptureAsync(attempt, status, reason, cancellationToken);
    }

    private async Task ResolveCaptureAsync(PaymentAttempt attempt, PaymentAttemptStatus status, string reason, CancellationToken cancellationToken)
    {
        if (status is PaymentAttemptStatus.ManualReview)
        {
            // F-23 / F-24: the booking is confirmed but not paid for. Never cancelled automatically: a person decides.
            LogCaptureFailed(logger, attempt.Id, attempt.OrderId, reason);
        }

        // A lost save is harmless: the attempt stays capturing, and the next run looks the capture up.
        if (attempt.ResolveCapture(status, reason, Change()).IsSuccess)
        {
            await store.TrySaveAsync(cancellationToken);
        }
    }

    private async Task VoidAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        // Saved as Voiding before the provider is asked: a crash leaves a void to look up, never a forgotten one.
        if (!attempt.BeginVoid(Change()).IsSuccess)
        {
            // A hold to release that cannot be (no provider payment id): never silent, a person must release it.
            LogCannotRelease(logger, attempt.Id, attempt.Status.ToString());
            return;
        }

        if (!await store.TrySaveAsync(cancellationToken))
        {
            return; // changed concurrently: the next run looks again
        }

        await SendVoidAsync(attempt, cancellationToken);
    }

    /// <summary>A void that was interrupted or whose outcome is unknown: look it up; repeat it (same key) only if still held.</summary>
    private async Task ResumeVoidAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), attempt.KnownProviderPayment(), cancellationToken);
        switch (outcome)
        {
            case PaymentOutcome.Voided:
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.Voided, "Found voided at the provider", cancellationToken);
                break;
            case PaymentOutcome.Canceled:
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.Canceled, "Found canceled at the provider", cancellationToken);
                break;
            case PaymentOutcome.AuthorizationExpired:
                await ResolveVoidAsync(attempt, PaymentAttemptStatus.Expired, "Found lapsed at the provider; nothing is held", cancellationToken);
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
            PaymentOutcome.AuthorizationExpired => (PaymentAttemptStatus.Expired, "The hold had already lapsed; nothing is held"),
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

    [LoggerMessage(Level = LogLevel.Error, EventName = "PaymentCaptureFailed", Message = "Alert: payment attempt {AttemptId} for order {OrderId} could not be charged for its confirmed booking ({Reason}); manual review")]
    private static partial void LogCaptureFailed(ILogger logger, Guid attemptId, Guid orderId, string reason);

    [LoggerMessage(Level = LogLevel.Error, EventName = "PaymentHoldNotReleasable", Message = "Alert: payment attempt {AttemptId} ({Status}) is to be released but cannot be voided; manual action needed")]
    private static partial void LogCannotRelease(ILogger logger, Guid attemptId, string status);

    [LoggerMessage(Level = LogLevel.Error, EventName = "StrayPaymentHold", Message = "Alert: payment attempt {AttemptId} is settled, but provider {ProviderId} reports a hold on it; releasing it")]
    private static partial void LogStrayHold(ILogger logger, Guid attemptId, string providerId);
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
/// Records Orders' request to charge its confirmed bookings (ADR 0007 inbox: once per event), for the reconciliation job
/// to capture. A request the attempt cannot take (not authorized, a release requested, more than held) is never
/// captured: it is recorded on the attempt's history and alerted for a person.
/// </summary>
internal sealed partial class OrderPaymentCaptureRequestedHandler(IPaymentAttemptStore store, TimeProvider timeProvider, ILogger<OrderPaymentCaptureRequestedHandler> logger)
    : IIntegrationEventHandler<OrderPaymentCaptureRequested>
{
    public const string Name = "payments.order-payment-capture-requested";
    public const string Actor = "system:orders";

    public async Task HandleAsync(OrderPaymentCaptureRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var change = new PaymentChange(now, Actor, integrationEvent.CorrelationId);
        if (await store.FindAsync(integrationEvent.PaymentId, cancellationToken) is { } attempt && attempt.OrderId == integrationEvent.OrderId)
        {
            if (!attempt.RequestCapture(integrationEvent.Amount, change).IsSuccess)
            {
                attempt.RecordFinding($"Capture of {integrationEvent.Amount.Amount} {integrationEvent.Amount.Currency.Value} requested but not possible ({attempt.Status})", change, null);
                LogCannotCapture(logger, attempt.Id, attempt.OrderId, attempt.Status.ToString());
            }
        }
        else
        {
            LogCannotCapture(logger, integrationEvent.PaymentId, integrationEvent.OrderId, "not found");
        }

        store.MarkConsumed(integrationEvent.EventId, Name, now);
        if (!await store.TrySaveAsync(cancellationToken) && !await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            throw new InvalidOperationException($"Payment attempt {integrationEvent.PaymentId} changed concurrently.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "PaymentCaptureNotPossible", Message = "Alert: order {OrderId} asked to charge payment attempt {AttemptId}, which cannot be captured ({Status}); manual action needed")]
    private static partial void LogCannotCapture(ILogger logger, Guid attemptId, Guid orderId, string status);
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
