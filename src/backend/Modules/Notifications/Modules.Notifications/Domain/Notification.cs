namespace TravelBooking.Modules.Notifications.Domain;

/// <summary>Where a customer notice stands (ADR 0024).</summary>
internal enum NotificationStatus
{
    /// <summary>To be sent (first time or a retry), at <see cref="Notification.NextAttemptAt"/>.</summary>
    Pending,

    /// <summary>Handed to the provider; if the process stops here, the outcome is unknown and it is sent again later.</summary>
    Sending,

    /// <summary>The provider accepted it (with its message id). Final until the provider's delivery events are processed.</summary>
    Accepted,

    Delivered,
    Bounced,

    /// <summary>Not sent on purpose: no contact any more (anonymised order), or the provider suppresses the address.</summary>
    Suppressed,

    /// <summary>Given up: refused by the provider, or still unsent after the last attempt (alert).</summary>
    Failed,
}

/// <summary>
/// One customer notice for one business event (ADR 0024): unique per (source event, kind), so a redelivered event never
/// creates a second one. It holds no personal data: the recipient is read from Customers when sending, and the values are
/// the event's non-personal facts (references, amounts).
/// </summary>
internal sealed class Notification
{
    public const int MaxAttempts = 10;
    public const int MaxKindLength = 50;
    public const int MaxValuesLength = 4000;
    public const int MaxErrorLength = 100;
    public const int MaxProviderMessageIdLength = 200;

    /// <summary>How long a send in progress is trusted before it counts as unknown and is retried.</summary>
    public static readonly TimeSpan SendingTimeout = TimeSpan.FromMinutes(5);

    private Notification()
    {
    }

    public Guid Id { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public Guid OrderId { get; private set; }

    public Guid SourceEventId { get; private set; }

    public int TemplateVersion { get; private set; }

    /// <summary>The template's values as JSON: non-personal facts only.</summary>
    public string Values { get; private set; } = string.Empty;

    public string Culture { get; private set; } = string.Empty;

    public NotificationStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>A reason code for the last failure (never provider text or an address).</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Notification For(string kind, Guid orderId, Guid sourceEventId, int templateVersion, string values, string culture, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        OrderId = orderId,
        SourceEventId = sourceEventId,
        TemplateVersion = templateVersion,
        Values = values,
        Culture = culture,
        Status = NotificationStatus.Pending,
        NextAttemptAt = at,
        CreatedAt = at,
        UpdatedAt = at,
    };

    /// <summary>Due to be sent: pending and due, or stuck in Sending past the timeout (an unknown outcome).</summary>
    public bool IsDue(DateTimeOffset now) => Status is NotificationStatus.Pending or NotificationStatus.Sending && NextAttemptAt <= now;

    /// <summary>
    /// Ends a due notice that already used every attempt (for example, sends interrupted again and again): Failed, so it
    /// is alerted on instead of retried forever. True if it was given up on.
    /// </summary>
    public bool GiveUpIfExhausted(DateTimeOffset at)
    {
        if (Attempts < MaxAttempts)
        {
            return false;
        }

        Finish(NotificationStatus.Failed, at, LastError ?? "attempts-exhausted");
        return true;
    }

    /// <summary>Saved before the provider is called, so a crash leaves a record that is sent again after the timeout.</summary>
    public void StartSending(DateTimeOffset at)
    {
        Status = NotificationStatus.Sending;
        Attempts++;
        NextAttemptAt = at + SendingTimeout;
        UpdatedAt = at;
    }

    public void Accepted(string providerMessageId, DateTimeOffset at) => Finish(NotificationStatus.Accepted, at, providerMessageId: providerMessageId);

    public void Suppressed(string reason, DateTimeOffset at) => Finish(NotificationStatus.Suppressed, at, reason);

    public void Rejected(string reason, DateTimeOffset at) => Finish(NotificationStatus.Failed, at, reason);

    /// <summary>
    /// An unknown or temporary failure: tried again with backoff (1, 2, 4 … minutes, at most an hour), until the last
    /// attempt, then Failed. True if it is given up on.
    /// </summary>
    public bool RetryLater(string reason, DateTimeOffset at)
    {
        if (Attempts >= MaxAttempts)
        {
            Finish(NotificationStatus.Failed, at, reason);
            return true;
        }

        Status = NotificationStatus.Pending;
        LastError = Bounded(reason, MaxErrorLength);
        NextAttemptAt = at + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Attempts - 1)));
        UpdatedAt = at;
        return false;
    }

    private void Finish(NotificationStatus status, DateTimeOffset at, string? error = null, string? providerMessageId = null)
    {
        Status = status;
        LastError = error is null ? null : Bounded(error, MaxErrorLength);
        ProviderMessageId = providerMessageId is null ? ProviderMessageId : Bounded(providerMessageId, MaxProviderMessageIdLength);
        UpdatedAt = at;
    }

    // Never let an over-long code from an adapter fail the save (which would leave an accepted message to be sent again).
    private static string Bounded(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
