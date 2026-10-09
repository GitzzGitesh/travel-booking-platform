using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>
/// One verified provider notification, stored once per provider event id (a unique constraint). It keeps what the core
/// needs to find the payment again: never the raw event, which can hold personal data (security rules, Q9). It is
/// processed by looking the payment up with the provider, never by trusting the event (ADR 0006).
/// </summary>
internal sealed class PaymentNotificationRecord
{
    public const int MaxOutcomeLength = 200;

    private PaymentNotificationRecord()
    {
    }

    public Guid Id { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public string EventId { get; private set; } = string.Empty;

    public PaymentNotificationKind Kind { get; private set; }

    public string? Reference { get; private set; }

    public string? ProviderPaymentId { get; private set; }

    public string? EventType { get; private set; }

    /// <summary>When the provider created the event (operators only: processing never orders by it).</summary>
    public DateTimeOffset? OccurredAt { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Not processed again before this, after a failed attempt (null: as soon as the job runs).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? Outcome { get; private set; }

    public static PaymentNotificationRecord For(string providerId, PaymentNotification notification, DateTimeOffset receivedAt) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = providerId,
        EventId = notification.EventId,
        Kind = notification.Kind,
        Reference = notification.Reference?.Value,
        ProviderPaymentId = notification.Payment?.ProviderId == providerId ? notification.Payment.Value : null,
        EventType = notification.EventType is { Length: > 0 and <= PaymentNotification.MaxEventTypeLength } type ? type : null,
        OccurredAt = notification.OccurredAt,
        ReceivedAt = receivedAt,
    };

    public void MarkProcessed(string outcome, DateTimeOffset at)
    {
        ProcessedAt = at;
        Outcome = outcome.Length <= MaxOutcomeLength ? outcome : outcome[..MaxOutcomeLength];
    }

    public void CountAttempt() => Attempts++;

    /// <summary>After a failed attempt: wait longer each time (2 s, 4 s, … at most five minutes), as the outbox does (ADR 0007).</summary>
    public void DeferRetry(DateTimeOffset now) => NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Attempts)));
}

internal interface IPaymentNotificationStore
{
    /// <summary>Adds the notification; false if this provider event was already recorded (a duplicate delivery).</summary>
    Task<bool> TryAddAsync(PaymentNotificationRecord notification, CancellationToken cancellationToken);

    /// <summary>Unprocessed notifications due at <paramref name="now"/> (not deferred), oldest first, tracked for <see cref="SaveAsync"/>.</summary>
    Task<IReadOnlyList<PaymentNotificationRecord>> FindUnprocessedAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>The attempt a notification is about: by our reference, else by the provider's payment id.</summary>
    Task<Guid?> FindAttemptAsync(string providerId, string? reference, string? providerPaymentId, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

internal enum PaymentNotificationReceipt
{
    /// <summary>Recorded for processing.</summary>
    Accepted,

    /// <summary>Already recorded: acknowledged, nothing else happens.</summary>
    Duplicate,

    /// <summary>Verified, but not an event the core acts on: acknowledged, not stored.</summary>
    Ignored,

    /// <summary>No composed provider sends notifications under this id.</summary>
    UnknownProvider,

    /// <summary>The signature, its age or the content is not acceptable: nothing is stored.</summary>
    Rejected,
}

/// <summary>
/// Receives a provider notification in the Api: verify first, record once, acknowledge fast (booking rules: webhooks).
/// Processing happens in the Worker.
/// </summary>
internal sealed partial class ReceivePaymentNotificationHandler(
    IEnumerable<IPaymentNotifications> verifiers,
    IPaymentNotificationStore store,
    TimeProvider timeProvider,
    ILogger<ReceivePaymentNotificationHandler> logger)
{
    public async Task<PaymentNotificationReceipt> HandleAsync(string providerId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (verifiers.FirstOrDefault(v => string.Equals(v.ProviderId, providerId, StringComparison.Ordinal)) is not { } verifier)
        {
            return PaymentNotificationReceipt.UnknownProvider;
        }

        var verified = verifier.Verify(body, headers);
        if (!verified.IsSuccess)
        {
            // A security event (security rules): the reason and provider only, never the body or the signature.
            LogRejected(logger, verifier.ProviderId, verified.Error);
            return PaymentNotificationReceipt.Rejected;
        }

        var notification = verified.Value;
        if (notification.Kind is PaymentNotificationKind.Ignored)
        {
            return PaymentNotificationReceipt.Ignored;
        }

        return await store.TryAddAsync(PaymentNotificationRecord.For(verifier.ProviderId, notification, timeProvider.GetUtcNow()), cancellationToken)
            ? PaymentNotificationReceipt.Accepted
            : PaymentNotificationReceipt.Duplicate;
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "PaymentNotificationRejected", Message = "Security: payment notification for provider {ProviderId} rejected ({Rejection})")]
    private static partial void LogRejected(ILogger logger, string providerId, PaymentNotificationRejection rejection);
}

/// <summary>
/// Processes recorded notifications in the Worker (ADR 0007): the attempt they concern is reconciled at once, which
/// means a lookup with the provider (never the event's content), so duplicates and out-of-order events are harmless.
/// Nothing is ever captured or authorized here.
/// </summary>
internal sealed partial class ProcessPaymentNotificationsJob(
    IPaymentNotificationStore store,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<ProcessPaymentNotificationsJob> logger) : IBackgroundJob
{
    public const string Name = "payments.process-notifications";
    public const int BatchSize = 50;
    public const int MaxAttempts = 10;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        foreach (var notification in await store.FindUnprocessedAsync(startedAt, BatchSize, cancellationToken))
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break;
            }

            handled++;
            notification.CountAttempt();
            try
            {
                notification.MarkProcessed(await ProcessAsync(notification, cancellationToken), timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, notification.ProviderId, notification.EventId, exception.GetType().Name);
                if (notification.Attempts >= MaxAttempts)
                {
                    // The attempt itself stays on the reconciliation work list; only this prompt is given up.
                    notification.MarkProcessed($"Gave up after {MaxAttempts} attempts ({exception.GetType().Name})", timeProvider.GetUtcNow());
                }
                else
                {
                    notification.DeferRetry(timeProvider.GetUtcNow()); // a failing notification never blocks the others, nor runs every 10 s
                }
            }

            await store.SaveAsync(cancellationToken);
        }

        return handled;
    }

    private async Task<string> ProcessAsync(PaymentNotificationRecord notification, CancellationToken cancellationToken)
    {
        if (notification.Kind is PaymentNotificationKind.Refund)
        {
            return "Refunds are not persisted yet: nothing to reconcile";
        }

        if (await store.FindAttemptAsync(notification.ProviderId, notification.Reference, notification.ProviderPaymentId, cancellationToken) is not { } attemptId)
        {
            LogNoAttempt(logger, notification.ProviderId, notification.EventId);
            return "No payment attempt of ours";
        }

        await using var scope = scopes.CreateAsyncScope();
        var hint = notification.ProviderPaymentId is { } id ? new ProviderPaymentRef(notification.ProviderId, id) : null;
        return await scope.ServiceProvider.GetRequiredService<PaymentAttemptReconciler>().ReconcileNotifiedAsync(attemptId, hint, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing payment notification {EventId} from {ProviderId} failed ({Error}); it will be retried")]
    private static partial void LogFailed(ILogger logger, string providerId, string eventId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment notification {EventId} from {ProviderId} matches no payment attempt of ours")]
    private static partial void LogNoAttempt(ILogger logger, string providerId, string eventId);
}
