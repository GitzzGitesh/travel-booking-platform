using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Notifications.Domain;
using TravelBooking.Modules.Notifications.Ports;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Notifications.Application;

internal interface INotificationStore
{
    /// <summary>Adds the notification; false if one already exists for this source event and kind (unique constraint).</summary>
    Task<bool> TryAddAsync(Notification notification, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Saves; false on a concurrent change (the work is left for the next run).</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The customer's notice for a settled booking (ADR 0024): recorded once per event (a redelivery finds the unique
/// constraint and does nothing), then sent by <see cref="SendNotificationsJob"/>. A booked hotel stay's facts are read
/// from Hotels (its frozen selection) and stored on the notice: the voucher (ADR 0030), never guest data.
/// </summary>
internal sealed partial class OrderBookingSettledHandler(INotificationStore store, IHotelStays stays, TimeProvider timeProvider, ILogger<OrderBookingSettledHandler> logger)
    : IIntegrationEventHandler<OrderBookingSettled>
{
    public async Task HandleAsync(OrderBookingSettled integrationEvent, CancellationToken cancellationToken)
    {
        var kind = integrationEvent.Outcome switch
        {
            BookingOutcome.Confirmed => NoticeTemplates.BookingConfirmed,
            BookingOutcome.PartiallyConfirmed => NoticeTemplates.BookingPartiallyConfirmed,
            _ => NoticeTemplates.BookingNotBooked,
        };
        var values = new BookingNoticeValues(
            integrationEvent.OrderId,
            integrationEvent.BookingReferences,
            integrationEvent.Charged?.Amount.ToString(CultureInfo.InvariantCulture),
            integrationEvent.Charged?.Currency.Value,
            await Stays(integrationEvent, cancellationToken));

        // The customer's language is not known yet (Q2): the default culture until it is.
        await store.TryAddAsync(
            Notification.For(kind, integrationEvent.OrderId, integrationEvent.EventId, NoticeTemplates.Version, values.ToJson(), NoticeTemplates.DefaultCulture, timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private async Task<IReadOnlyList<StayNotice>?> Stays(OrderBookingSettled integrationEvent, CancellationToken cancellationToken)
    {
        var hotels = (integrationEvent.Items ?? []).Where(i => i.Product == "Hotel").ToList();
        if (hotels.Count == 0)
        {
            return null;
        }

        // The confirmation is never lost for a stay's facts: a stay that cannot be read is left out, with an alert.
        var facts = new List<StayNotice>();
        foreach (var item in hotels)
        {
            HotelStay? stay;
            try
            {
                stay = await stays.GetStayAsync(item.SelectedOfferId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogStayUnreadable(logger, integrationEvent.OrderId, exception.GetType().Name);
                continue;
            }

            if (stay is null)
            {
                LogStayUnreadable(logger, integrationEvent.OrderId, "not found");
                continue;
            }

            facts.Add(StayFacts.From(stay, item));
        }

        return facts;
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "VoucherStayUnreadable",
        Message = "Alert: order {OrderId}'s confirmation is sent without a hotel stay's details ({Reason}); send the voucher by hand")]
    private static partial void LogStayUnreadable(ILogger logger, Guid orderId, string reason);
}

internal static class StayFacts
{
    /// <summary>The stay's facts from Hotels; its cancellation terms from the order (what the customer agreed to).</summary>
    public static StayNotice From(HotelStay stay, BookedItem item) => item.CancellationRefundable is { } refundable
        ? new(
            stay.PropertyName,
            stay.AddressLine,
            stay.CheckIn,
            stay.CheckOut,
            stay.Nights,
            stay.Room,
            stay.Board,
            refundable && item.FreeCancellationUntil is not null,
            item.FreeCancellationUntil,
            item.CancellationPenalty?.ToString(CultureInfo.InvariantCulture),
            item.CancellationPenalty is null ? null : item.Currency,
            stay.TimeZone)
        : From(stay);

    // An order without recorded terms (booked before they were): the booked selection's (frozen) terms.
    private static StayNotice From(HotelStay stay) => new(
        stay.PropertyName,
        stay.AddressLine,
        stay.CheckIn,
        stay.CheckOut,
        stay.Nights,
        stay.Room,
        stay.Board,
        stay.Cancellation.Refundable && stay.Cancellation.FreeCancellationUntil is not null,
        stay.Cancellation.FreeCancellationUntil,
        stay.Cancellation.PenaltyAfterDeadline?.Amount.ToString(CultureInfo.InvariantCulture),
        stay.Cancellation.PenaltyAfterDeadline?.Currency.Value,
        stay.TimeZone);
}

/// <summary>
/// The customer's notice for a refund's outcome (ADR 0024, ADR 0027): sent, or delayed (a failed refund is followed up by
/// operations and never told to the customer as done). Once per event (unique constraint).
/// </summary>
internal sealed class PaymentRefundSettledHandler(INotificationStore store, TimeProvider timeProvider) : IIntegrationEventHandler<Payments.Contracts.PaymentRefundSettled>
{
    public async Task HandleAsync(Payments.Contracts.PaymentRefundSettled integrationEvent, CancellationToken cancellationToken)
    {
        // A failed refund is never told as done: the customer hears it is delayed, while operations follow it up (alert).
        var values = new BookingNoticeValues(integrationEvent.OrderId, [], integrationEvent.Amount.Amount.ToString(CultureInfo.InvariantCulture),
            integrationEvent.Amount.Currency.Value);
        await store.TryAddAsync(
            Notification.For(integrationEvent.Succeeded ? NoticeTemplates.RefundCompleted : NoticeTemplates.RefundDelayed, integrationEvent.OrderId,
                integrationEvent.EventId, NoticeTemplates.Version, values.ToJson(), NoticeTemplates.DefaultCulture, timeProvider.GetUtcNow()),
            cancellationToken);
    }
}

/// <summary>
/// The customer's notice that bookings were cancelled (ADR 0027). It names no refund amount: the refund, if any, still
/// needs a second person's approval and may be nothing (fee, supplier refund) or rejected.
/// </summary>
internal sealed class OrderCancellationRecordedHandler(INotificationStore store, TimeProvider timeProvider) : IIntegrationEventHandler<OrderCancellationRecorded>
{
    public async Task HandleAsync(OrderCancellationRecorded integrationEvent, CancellationToken cancellationToken)
    {
        var values = new BookingNoticeValues(integrationEvent.OrderId, [], null, null); // no amount promised before approval
        await store.TryAddAsync(
            Notification.For(NoticeTemplates.BookingCancelled, integrationEvent.OrderId, integrationEvent.EventId, NoticeTemplates.Version, values.ToJson(),
                NoticeTemplates.DefaultCulture, timeProvider.GetUtcNow()),
            cancellationToken);
    }
}

/// <summary>The customer's acknowledgement of a cancellation request (ADR 0029): nothing is cancelled or promised yet.</summary>
internal sealed class CustomerCancellationRequestedHandler(INotificationStore store, TimeProvider timeProvider)
    : IIntegrationEventHandler<CustomerCancellationRequested>
{
    public Task HandleAsync(CustomerCancellationRequested integrationEvent, CancellationToken cancellationToken) =>
        store.TryAddAsync(
            Notification.For(NoticeTemplates.CancellationRequested, integrationEvent.OrderId, integrationEvent.EventId, NoticeTemplates.Version,
                new BookingNoticeValues(integrationEvent.OrderId, [], null, null).ToJson(), NoticeTemplates.DefaultCulture, timeProvider.GetUtcNow()),
            cancellationToken);
}

/// <summary>A declined cancellation request (ADR 0029): the customer is told support will contact them, never the internal reason.</summary>
internal sealed class CustomerCancellationDeclinedHandler(INotificationStore store, TimeProvider timeProvider)
    : IIntegrationEventHandler<CustomerCancellationDeclined>
{
    public Task HandleAsync(CustomerCancellationDeclined integrationEvent, CancellationToken cancellationToken) =>
        store.TryAddAsync(
            Notification.For(NoticeTemplates.CancellationDeclined, integrationEvent.OrderId, integrationEvent.EventId, NoticeTemplates.Version,
                new BookingNoticeValues(integrationEvent.OrderId, [], null, null).ToJson(), NoticeTemplates.DefaultCulture, timeProvider.GetUtcNow()),
            cancellationToken);
}

/// <summary>
/// Sends due notices (ADR 0024), each on its own: saved as Sending before the provider is called, so a crash leads to
/// another send later (at least once). Without a configured provider nothing is sent and notices wait, which is safe.
/// The recipient is read from Customers at this moment and never stored here; an anonymised order is suppressed.
/// </summary>
internal sealed partial class SendNotificationsJob(
    INotificationStore store, IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<SendNotificationsJob> logger) : IBackgroundJob
{
    public const string Name = "notifications.send";
    public const int BatchSize = 20;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        var due = await store.FindDueAsync(startedAt, BatchSize, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        await using (var probe = scopes.CreateAsyncScope())
        {
            if (probe.ServiceProvider.GetService<IEmailSender>() is null)
            {
                LogNoProvider(logger);
                return 0; // they wait, safely, until a provider is configured
            }
        }

        foreach (var id in due)
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break;
            }

            // Each notice in its own scope: one failure never leaks tracked changes into the next.
            await using var scope = scopes.CreateAsyncScope();
            if (await SendAsync(id, scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IEmailSender>(), cancellationToken))
            {
                handled++;
            }
        }

        return handled;
    }

    private async Task<bool> SendAsync(Guid id, IServiceProvider services, IEmailSender sender, CancellationToken cancellationToken)
    {
        var notices = services.GetRequiredService<INotificationStore>();
        var now = timeProvider.GetUtcNow();
        if (await notices.FindAsync(id, cancellationToken) is not { } notice || !notice.IsDue(now))
        {
            return false;
        }

        if (await services.GetRequiredService<IOrderContacts>().FindForNoticeAsync(notice.OrderId, cancellationToken) is not { } contact)
        {
            notice.Suppressed("no-contact", now);
            return await notices.TrySaveAsync(cancellationToken);
        }

        if (notice.GiveUpIfExhausted(now))
        {
            LogFailed(logger, notice.Id, notice.OrderId, notice.Kind, notice.LastError!);
            return await notices.TrySaveAsync(cancellationToken);
        }

        // Rendered before anything is sent: a notice that cannot be rendered fails for good (and is alerted on).
        RenderedNotice rendered;
        try
        {
            rendered = NoticeTemplates.Render(notice.Kind, notice.Culture, BookingNoticeValues.FromJson(notice.Values));
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            notice.Rejected("render-failed", now);
            LogFailed(logger, notice.Id, notice.OrderId, notice.Kind, "render-failed");
            return await notices.TrySaveAsync(cancellationToken);
        }

        notice.StartSending(now);
        if (!await notices.TrySaveAsync(cancellationToken))
        {
            return false; // another run has it
        }

        EmailSendResult result;
        try
        {
            result = await sender.SendAsync(new EmailMessage(notice.Id, contact.Email, rendered.Subject, rendered.HtmlBody, rendered.TextBody), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new EmailSendResult.Unknown(exception.GetType().Name); // an exception after the call started: unknown
        }

        var at = timeProvider.GetUtcNow();
        switch (result)
        {
            case EmailSendResult.Accepted accepted:
                notice.Accepted(accepted.ProviderMessageId, at);
                break;
            case EmailSendResult.Rejected { Suppressed: true } suppressed:
                notice.Suppressed(suppressed.Reason, at);
                break;
            case EmailSendResult.Rejected rejected:
                notice.Rejected(rejected.Reason, at);
                LogFailed(logger, notice.Id, notice.OrderId, notice.Kind, rejected.Reason);
                break;
            case EmailSendResult.Unknown unknown:
                if (notice.RetryLater(unknown.Reason, at))
                {
                    LogFailed(logger, notice.Id, notice.OrderId, notice.Kind, unknown.Reason);
                }

                break;
        }

        // If this save loses a race, the notice stays Sending and is sent again after the timeout (at least once).
        return await notices.TrySaveAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "NotificationProviderMissing",
        Message = "Customer notices are waiting: no email provider is configured")]
    private static partial void LogNoProvider(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, EventName = "NotificationFailed",
        Message = "Alert: customer notice {NotificationId} ({Kind}) for order {OrderId} was not sent: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid notificationId, Guid orderId, string kind, string reason);
}
