using TravelBooking.Modules.Notifications.Domain;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// A notice's delivery state (ADR 0024): retried with backoff, a send interrupted mid-way is retried after the timeout,
/// and it ends Failed after the last attempt instead of being retried forever. Pure domain, controlled time.
/// </summary>
public sealed class NotificationTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unknown_outcome_is_retried_with_backoff_then_given_up_on_after_the_last_attempt()
    {
        var notice = New();
        var at = _now;
        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt < Notification.MaxAttempts; attempt++)
        {
            notice.StartSending(at);
            notice.RetryLater("timeout", at).ShouldBeFalse();
            waits.Add(notice.NextAttemptAt - at);
            at = notice.NextAttemptAt;
        }

        waits.Take(5).ShouldBe([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16)]);
        waits.ShouldAllBe(w => w <= TimeSpan.FromHours(1));

        notice.StartSending(at);
        notice.RetryLater("timeout", at).ShouldBeTrue();
        (notice.Status, notice.Attempts).ShouldBe((NotificationStatus.Failed, Notification.MaxAttempts));
    }

    [Fact]
    public void A_send_interrupted_mid_way_is_due_again_after_the_timeout_and_never_more_than_the_attempts_allow()
    {
        var notice = New();
        notice.StartSending(_now); // the process stops here: still Sending

        notice.IsDue(_now.AddMinutes(4)).ShouldBeFalse();
        notice.IsDue(_now + Notification.SendingTimeout).ShouldBeTrue();

        for (var attempt = 2; attempt <= Notification.MaxAttempts; attempt++)
        {
            notice.GiveUpIfExhausted(_now).ShouldBeFalse();
            notice.StartSending(_now);
        }

        notice.GiveUpIfExhausted(_now).ShouldBeTrue();
        (notice.Status, notice.LastError).ShouldBe((NotificationStatus.Failed, "attempts-exhausted"));
    }

    [Fact]
    public void An_adapters_overlong_code_never_fails_the_save()
    {
        var notice = New();
        notice.StartSending(_now);
        notice.Accepted(new string('x', 500), _now);
        notice.ProviderMessageId!.Length.ShouldBe(Notification.MaxProviderMessageIdLength);

        var refused = New();
        refused.Rejected(new string('y', 500), _now);
        refused.LastError!.Length.ShouldBe(Notification.MaxErrorLength);
    }

    private static Notification New() =>
        Notification.For("booking-confirmed", Guid.NewGuid(), Guid.NewGuid(), 1, "{}", "en", _now);
}
