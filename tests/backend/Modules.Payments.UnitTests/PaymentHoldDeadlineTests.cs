using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>
/// Hold deadlines (ADR 0025): the time funds were first held is recorded, and a hold still neither captured nor released
/// raises one warning before it lapses. The watch never captures or releases anything itself.
/// </summary>
public sealed class PaymentHoldDeadlineTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly PaymentHoldOptions _holds = new() { Lifetime = TimeSpan.FromDays(7), WarningBefore = TimeSpan.FromHours(48) };

    [Fact]
    public void The_first_authorization_time_is_kept()
    {
        var attempt = Authorized();
        attempt.AuthorizedAt.ShouldBe(_now);
        _holds.ExpiresAt(attempt).ShouldBe(_now.AddDays(7));

        attempt.Resolve(PaymentAttemptStatus.ManualReview, "review", Change(_now.AddDays(1)), "mockpay", "pay_1");
        attempt.ResolveReview(PaymentAttemptStatus.Authorized, "settled by lookup", Change(_now.AddDays(2)), "mockpay", "pay_1");

        attempt.AuthorizedAt.ShouldBe(_now); // the hold dates from the first authorization, not a later return to Authorized
    }

    [Fact]
    public async Task A_hold_still_unsettled_at_its_warning_time_raises_one_warning()
    {
        var held = Authorized();
        var declined = PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-2", Price, Change(_now));
        declined.Resolve(PaymentAttemptStatus.Declined, "declined", Change(_now), "mockpay", "pay_2");
        _store.Attempts.AddRange([held, declined]);

        _clock.Advance(TimeSpan.FromDays(5) - TimeSpan.FromMinutes(1));
        (await Job().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0); // not yet

        _clock.Advance(TimeSpan.FromMinutes(1));
        (await Job().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        held.HoldWarningRaisedAt.ShouldBe(_clock.GetUtcNow());
        held.Status.ShouldBe(PaymentAttemptStatus.Authorized); // nothing captured or released by the watch

        (await Job().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0); // once per attempt
        declined.HoldWarningRaisedAt.ShouldBeNull(); // nothing held
    }

    // A review reached straight from authorizing (e.g. the provider reported another amount) may hold funds without ever
    // showing Authorized: its hold is dated from the attempt's start, the earliest it can have begun, so the warning comes
    // early rather than late. Settled back to Authorized by a lookup, it keeps that earliest start.
    [Fact]
    public async Task A_review_that_never_showed_authorized_is_watched_from_the_attempts_start()
    {
        var reviewed = PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-3", Price, Change(_now));
        reviewed.Resolve(PaymentAttemptStatus.ManualReview, "another amount", Change(_now.AddHours(1)), "mockpay", "pay_3");
        _store.Attempts.Add(reviewed);
        (reviewed.AuthorizedAt, reviewed.HoldStartedAt, _holds.ExpiresAt(reviewed)).ShouldBe((null, _now, _now.AddDays(7)));

        _clock.Advance(TimeSpan.FromDays(5));
        (await Job().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        reviewed.ResolveReview(PaymentAttemptStatus.Authorized, "settled by lookup", Change(_now.AddDays(5)), "mockpay", "pay_3").IsSuccess.ShouldBeTrue();
        reviewed.AuthorizedAt.ShouldBe(_now); // the start, never the review time
    }

    [Fact]
    public async Task A_warning_whose_save_lost_a_race_is_not_reported()
    {
        _store.Attempts.Add(Authorized());
        _clock.Advance(TimeSpan.FromDays(6));
        _store.FailNextSave = true;

        // The real store forgets the unsaved change, so the next run warns again (at most a repeated alert, never a lost one).
        (await Job().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Theory]
    [InlineData(7, 48, true)]
    [InlineData(7, 168, false)] // a warning as long as the hold itself
    [InlineData(0, 1, false)]
    public void Hold_settings_are_validated(int lifetimeDays, int warningHours, bool valid) =>
        new PaymentHoldOptions { Lifetime = TimeSpan.FromDays(lifetimeDays), WarningBefore = TimeSpan.FromHours(warningHours) }.IsValid().ShouldBe(valid);

    private static Money Price => new(270m, new CurrencyCode("XTS"));

    private WatchExpiringHoldsJob Job() => new(_store, Options.Create(_holds), _clock, NullLogger<WatchExpiringHoldsJob>.Instance);

    private static PaymentAttempt Authorized()
    {
        var attempt = PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-1", Price, Change(_now));
        attempt.Resolve(PaymentAttemptStatus.Authorized, "authorized", Change(_now), "mockpay", "pay_1");
        return attempt;
    }

    private static PaymentChange Change(DateTimeOffset at) => new(at, "customer", "trace-1");
}
