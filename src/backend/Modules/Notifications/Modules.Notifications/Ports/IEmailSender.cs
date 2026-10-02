namespace TravelBooking.Modules.Notifications.Ports;

/// <summary>
/// The email provider port (ADR 0024): adapters (Azure Communication Services Email first, a dedicated transactional
/// provider as the fallback) live outside the core and map their own errors. One call sends one message, identified by
/// our <see cref="EmailMessage.MessageId"/>, which an adapter passes to the provider as its idempotency or operation id
/// where the provider supports one.
/// </summary>
public interface IEmailSender
{
    /// <summary>A stable id for logs and configuration (e.g. "recording", "acs").</summary>
    string ProviderId { get; }

    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <param name="MessageId">Ours: the notification id.</param>
/// <param name="To">Personal data: never logged.</param>
public sealed record EmailMessage(Guid MessageId, string To, string Subject, string HtmlBody, string TextBody);

public abstract record EmailSendResult
{
    private EmailSendResult()
    {
    }

    /// <summary>The provider accepted the message for delivery.</summary>
    public sealed record Accepted(string ProviderMessageId) : EmailSendResult;

    /// <summary>Refused for good (e.g. an invalid or suppressed address): never sent again. <c>Reason</c> is a short code, never provider text.</summary>
    public sealed record Rejected(string Reason, bool Suppressed) : EmailSendResult;

    /// <summary>A timeout, throttling or a temporary failure: sent again later (a duplicate email is acceptable; a lost one is not). <c>Reason</c> is a short code.</summary>
    public sealed record Unknown(string Reason) : EmailSendResult;
}
