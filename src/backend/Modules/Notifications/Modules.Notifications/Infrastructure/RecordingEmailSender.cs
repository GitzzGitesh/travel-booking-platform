using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using TravelBooking.Modules.Notifications.Ports;

namespace TravelBooking.Modules.Notifications.Infrastructure;

/// <summary>
/// The deterministic stand-in for an email provider (ADR 0024), for Development, Staging and tests: it keeps the last
/// messages in memory and sends nothing over the network. Scenarios are chosen by the recipient's domain, never by
/// randomness: <c>unknown.test.invalid</c> (the outcome is unknown), <c>rejected.test.invalid</c> (refused for good),
/// <c>suppressed.test.invalid</c> (a suppressed address); any other address is accepted.
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    public const string UnknownDomain = "unknown.test.invalid";
    public const string RejectedDomain = "rejected.test.invalid";
    public const string SuppressedDomain = "suppressed.test.invalid";
    private const int _kept = 1000;

    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public RecordingEmailSender(IHostEnvironment environment)
    {
        // Like the payment mock: never in a production-like environment, where customers would silently get nothing.
        if (!environment.IsDevelopment() && !environment.IsStaging())
        {
            throw new InvalidOperationException("The recording email sender runs only in Development and Staging.");
        }
    }

    public string ProviderId => "recording";

    /// <summary>The messages accepted so far, oldest first (the last 1000).</summary>
    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var domain = message.To[(message.To.LastIndexOf('@') + 1)..];
        EmailSendResult result = domain switch
        {
            UnknownDomain => new EmailSendResult.Unknown("timeout"),
            RejectedDomain => new EmailSendResult.Rejected("invalid-recipient", Suppressed: false),
            SuppressedDomain => new EmailSendResult.Rejected("suppressed", Suppressed: true),
            _ => new EmailSendResult.Accepted($"recording-{message.MessageId:N}"),
        };

        if (result is EmailSendResult.Accepted)
        {
            _sent.Enqueue(message);
            while (_sent.Count > _kept && _sent.TryDequeue(out _))
            {
            }
        }

        return Task.FromResult(result);
    }
}
