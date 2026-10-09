using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>
/// A provider notification whose processing fails is retried with a growing wait (as the outbox is, ADR 0007), not on
/// every 10-second run: it never blocks the others, and is given up after <see cref="ProcessPaymentNotificationsJob.MaxAttempts"/>.
/// The payment attempt itself stays on the reconciliation work list.
/// </summary>
public sealed class PaymentNotificationRetryTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FailingStore _store = new();

    [Fact]
    public async Task A_failing_notification_waits_longer_after_each_attempt_and_is_given_up_after_the_limit()
    {
        var notification = _store.Add("evt_failing", _now);

        await Job().RunOnceAsync(Ct);
        (notification.Attempts, notification.NextAttemptAt).ShouldBe((1, _now.AddSeconds(2)));

        await Job().RunOnceAsync(Ct); // before it is due again: left alone
        notification.Attempts.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Job().RunOnceAsync(Ct);
        (notification.Attempts, notification.NextAttemptAt).ShouldBe((2, _clock.GetUtcNow().AddSeconds(4)));

        // Doubling, then at most five minutes between attempts, until the limit.
        while (notification.ProcessedAt is null)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            await Job().RunOnceAsync(Ct);
            (notification.NextAttemptAt - _clock.GetUtcNow()).ShouldNotBeNull().ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        }

        notification.Attempts.ShouldBe(ProcessPaymentNotificationsJob.MaxAttempts);
        notification.Outcome.ShouldNotBeNull().ShouldStartWith($"Gave up after {ProcessPaymentNotificationsJob.MaxAttempts} attempts");
    }

    [Fact]
    public async Task A_deferred_notification_never_holds_back_one_that_is_due()
    {
        var failing = _store.Add("evt_failing", _now);
        await Job().RunOnceAsync(Ct);
        var later = _store.Add("evt_refund", _now.AddSeconds(1), PaymentNotificationKind.Refund); // processed without a lookup

        await Job().RunOnceAsync(Ct);

        later.ProcessedAt.ShouldNotBeNull();
        failing.Attempts.ShouldBe(1);
    }

    private ProcessPaymentNotificationsJob Job() =>
        new(_store, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<ProcessPaymentNotificationsJob>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Finding the attempt a payment notification is about always fails here; due-ness is filtered as the SQL store does.
    private sealed class FailingStore : IPaymentNotificationStore
    {
        private readonly List<PaymentNotificationRecord> _notifications = [];

        public PaymentNotificationRecord Add(string eventId, DateTimeOffset receivedAt, PaymentNotificationKind kind = PaymentNotificationKind.Payment)
        {
            var record = PaymentNotificationRecord.For("mockpay", new PaymentNotification(eventId, kind, new PaymentReference($"pay-{Guid.NewGuid():N}"), null), receivedAt);
            _notifications.Add(record);
            return record;
        }

        public Task<bool> TryAddAsync(PaymentNotificationRecord notification, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentNotificationRecord>> FindUnprocessedAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentNotificationRecord>>([.. _notifications
                .Where(n => n.ProcessedAt is null && (n.NextAttemptAt is null || n.NextAttemptAt <= now))
                .OrderBy(n => n.ReceivedAt)
                .Take(limit)]);

        public Task<Guid?> FindAttemptAsync(string providerId, string? reference, string? providerPaymentId, CancellationToken cancellationToken) =>
            throw new TimeoutException("Simulated: the attempt could not be read.");

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
