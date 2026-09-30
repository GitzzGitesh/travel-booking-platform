using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>
/// Background reconciliation and hold release (payment-lifecycle.md; F-21, F-22): open attempts are looked up, never
/// authorized again; a hold Orders will not use is voided once, keyed by the attempt; an interrupted or unknown void is
/// looked up and repeated with the same key only while the payment is still held.
/// </summary>
public sealed class PaymentAttemptReconciliationTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Money _total = new(270m, new CurrencyCode("XTS"));
    private static readonly Guid _orderId = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly ScriptedProvider _provider = new();

    [Fact]
    public async Task An_unknown_authorization_found_authorized_is_recorded_and_kept_without_a_release_request()
    {
        var attempt = await Attempt(Unknown());
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized);

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        (_provider.Authorizations, _provider.Voids.Count).ShouldBe((1, 0));
        attempt.Events[^1].Actor.ShouldBe(PaymentAttemptReconciler.Actor);
    }

    [Fact]
    public async Task An_unknown_authorization_found_declined_holds_nothing_and_is_never_voided()
    {
        var attempt = await Attempt(Unknown());
        _provider.OnLookup = reference => Found(reference, PaymentState.Declined);
        await RequestRelease(attempt);

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Declined);
        _provider.Voids.ShouldBeEmpty();
    }

    [Fact]
    public async Task F22_an_unused_authorized_hold_is_voided_once()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Voided));

        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Voided);
        _provider.Voids.ShouldHaveSingleItem().Key.Value.ShouldBe($"{attempt.Reference}:void");
        attempt.Events.Select(e => e.ToStatus).TakeLast(3).ShouldBe(["Authorized", "Voiding", "Voided"]);
    }

    [Fact]
    public async Task A_hold_without_a_release_request_is_left_alone()
    {
        var attempt = await Attempt(Authorized());

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        (_provider.Lookups, _provider.Voids.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_void_that_timed_out_is_looked_up_and_repeated_with_the_same_key_only_while_still_held()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        _provider.OnVoid = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "stub"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        attempt.Status.ShouldBe(PaymentAttemptStatus.VoidUnknown);

        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized);
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Voided));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Voided);
        _provider.Voids.Select(v => v.Key).Distinct().ShouldHaveSingleItem();
        _provider.Voids.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(PaymentState.Voided, "Voided")]
    [InlineData(PaymentState.Canceled, "Canceled")]
    [InlineData(PaymentState.Captured, "ManualReview")] // never captured by us: a person looks at it
    internal async Task A_void_with_an_unknown_outcome_is_settled_by_lookup_without_a_second_void(PaymentState found, string expected)
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        _provider.OnVoid = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        _provider.OnLookup = reference => Found(reference, found);
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ToString().ShouldBe(expected);
        _provider.Voids.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_void_interrupted_by_a_crash_is_looked_up_on_restart()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        attempt.BeginVoid(new PaymentChange(_now, "crashed-worker", null)); // saved as Voiding, then the process died
        _provider.OnLookup = reference => Found(reference, PaymentState.Voided);

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Voided);
        _provider.Voids.ShouldBeEmpty();
    }

    [Fact]
    public async Task F21_an_unfinished_challenge_Orders_will_not_use_is_canceled()
    {
        var attempt = await Attempt(details => Ok(Snapshot(details.Reference, PaymentState.RequiresAction)));
        await RequestRelease(attempt);
        _provider.OnLookup = reference => Found(reference, PaymentState.RequiresAction);
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Canceled));

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Canceled);
        _provider.Voids.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_unfinished_challenge_without_a_release_request_is_only_looked_up()
    {
        var attempt = await Attempt(details => Ok(Snapshot(details.Reference, PaymentState.RequiresAction)));
        _provider.OnLookup = reference => Found(reference, PaymentState.RequiresAction);

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.ActionRequired);
        (_provider.Lookups, _provider.Voids.Count).ShouldBe((1, 0));
    }

    [Theory]
    [InlineData(ProviderErrorKind.InvalidRequest)]
    [InlineData(ProviderErrorKind.IdempotencyConflict)]
    internal async Task A_refused_void_goes_to_manual_review(ProviderErrorKind refusal)
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        _provider.OnVoid = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(refusal, "stub"));

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
    }

    [Fact]
    public async Task The_job_waits_for_open_attempts_to_settle_and_then_works_through_them()
    {
        var open = await Attempt(Unknown());
        var released = await Attempt(Authorized(), orderId: Guid.NewGuid());
        await RequestRelease(released);
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized);
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Voided));

        (await Job().RunOnceAsync(Ct)).ShouldBe(1); // only the release: the open attempt changed less than a minute ago
        _clock.Advance(TimeSpan.FromMinutes(1));
        (await Job().RunOnceAsync(Ct)).ShouldBe(1);
        (await Job().RunOnceAsync(Ct)).ShouldBe(0); // a repeat finds nothing to do

        (open.Status, released.Status).ShouldBe((PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Voided));
        _provider.Voids.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_release_request_is_recorded_once_per_event()
    {
        var attempt = await Attempt(Authorized());
        var request = Release(attempt);

        await ReleaseHandler().HandleAsync(request, Ct);
        await ReleaseHandler().HandleAsync(request, Ct); // redelivered by the outbox

        attempt.ReleaseRequestedAt.ShouldNotBeNull();
        attempt.Events.Count(e => e.Reason.StartsWith("Release requested", StringComparison.Ordinal)).ShouldBe(1);
        (attempt.Events[^1].Actor, attempt.Events[^1].CorrelationId).ShouldBe((OrderPaymentReleaseRequestedHandler.Actor, "trace-9"));
        _store.Consumed.ShouldBe(1);
    }

    [Fact]
    public async Task A_release_request_for_another_orders_payment_changes_nothing()
    {
        var attempt = await Attempt(Authorized());

        await ReleaseHandler().HandleAsync(Release(attempt) with { OrderId = Guid.NewGuid() }, Ct);

        attempt.ReleaseRequestedAt.ShouldBeNull();
        _store.Consumed.ShouldBe(1);
    }

    [Fact]
    public async Task A_release_request_that_loses_a_race_fails_so_the_outbox_delivers_it_again()
    {
        var attempt = await Attempt(Authorized());
        _store.FailNextSave = true;

        await Should.ThrowAsync<InvalidOperationException>(() => ReleaseHandler().HandleAsync(Release(attempt), Ct));

        _store.Consumed.ShouldBe(0);
    }

    [Fact]
    public async Task A_hold_being_released_is_never_reported_as_bookable_to_a_repeat_of_its_request()
    {
        var key = $"key-{Guid.NewGuid():N}";
        _provider.OnAuthorize = Authorized();
        var request = new OrderPaymentRequest(_orderId, "cust-1", key, _total, "pm_test");
        var paymentId = (await Handler().AuthorizeAsync(request, Ct)).Value.PaymentId;
        var attempt = _store.Attempts.Single(a => a.Id == paymentId);
        await RequestRelease(attempt); // the offer expired; the void has not run yet

        var replay = (await Handler().AuthorizeAsync(request, Ct)).Value;
        var resumed = await Handler().ResumeAsync(_orderId, "cust-1", key, null, Ct);

        replay.Status.ShouldBe(OrderPaymentStatus.Pending); // never Authorized: Orders must not start booking on it
        resumed!.Status.ShouldBe(OrderPaymentStatus.Pending);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
    }

    [Fact]
    public async Task An_unfinished_challenge_being_released_hands_out_no_customer_action()
    {
        var key = $"key-{Guid.NewGuid():N}";
        _provider.OnAuthorize = details => Ok(Snapshot(details.Reference, PaymentState.RequiresAction) with { CustomerActionToken = new CustomerActionToken("secret_1") });
        var paymentId = (await Handler().AuthorizeAsync(new OrderPaymentRequest(_orderId, "cust-1", key, _total, "pm_test"), Ct)).Value.PaymentId;
        var attempt = _store.Attempts.Single(a => a.Id == paymentId);
        await RequestRelease(attempt);
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.RequiresAction) with { CustomerActionToken = new CustomerActionToken("secret_1") });

        var resumed = await Handler().ResumeAsync(_orderId, "cust-1", key, null, Ct);

        (resumed!.Status, resumed.CustomerAction).ShouldBe((OrderPaymentStatus.Pending, null));
    }

    [Fact]
    public async Task A_notification_revealing_a_hold_on_a_declined_attempt_releases_it_once_and_records_it()
    {
        var attempt = await Attempt(details => Ok(Snapshot(details.Reference, PaymentState.Declined)));
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized); // e.g. the declined payment confirmed again
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Voided));

        var outcome = await Reconciler().ReconcileNotifiedAsync(attempt.Id, null, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Declined); // still final: it never becomes the order's live attempt
        _provider.Voids.ShouldHaveSingleItem().Key.Value.ShouldBe($"{attempt.Reference}:stray-void");
        attempt.Events[^1].Reason.ShouldStartWith("Hold found on a Declined attempt; released");
        outcome.ShouldContain("released");
    }

    [Fact]
    public async Task A_hold_on_a_settled_attempt_that_cannot_be_released_fails_the_notification_for_a_retry()
    {
        var attempt = await Attempt(details => Ok(Snapshot(details.Reference, PaymentState.Declined)));
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized);
        _provider.OnVoid = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));

        await Should.ThrowAsync<InvalidOperationException>(() => Reconciler().ReconcileNotifiedAsync(attempt.Id, null, Ct));

        attempt.Events[^1].Reason.ShouldContain("NOT released");
    }

    [Fact]
    public async Task A_notification_about_a_settled_attempt_that_holds_nothing_changes_nothing()
    {
        var attempt = await Attempt(details => Ok(Snapshot(details.Reference, PaymentState.Declined)));
        var events = attempt.Events.Count;
        _provider.OnLookup = reference => Found(reference, PaymentState.Declined);

        await Reconciler().ReconcileNotifiedAsync(attempt.Id, null, Ct);

        (attempt.Events.Count, _provider.Voids.Count).ShouldBe((events, 0));
    }

    [Fact]
    public async Task A_voided_hold_that_had_already_lapsed_ends_expired()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);
        _provider.OnVoid = details => Ok(Snapshot(details.Reference, PaymentState.Expired));

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Expired);
    }

    [Fact]
    public async Task The_live_attempt_is_reported_to_orders_with_its_release_state()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);

        var live = await Handler().FindLiveAsync(_orderId, Ct);

        live.ShouldBe(new LiveOrderPayment(attempt.Id, OrderPaymentStatus.Pending, ReleaseRequested: true));
        (await Handler().FindLiveAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    // ---------- Capture after a confirmed booking (ADR 0005; F-23, F-24, F-25) ----------

    [Fact]
    public async Task A_confirmed_booking_is_charged_once_with_the_attempts_capture_key()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = details => Ok(Snapshot(details.Reference, PaymentState.Captured) with { Captured = details.Amount });

        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Captured);
        var capture = _provider.Captures.ShouldHaveSingleItem();
        (capture.Key.Value, capture.Amount).ShouldBe(($"{attempt.Reference}:capture", _total));
        attempt.Events.Select(e => e.ToStatus).TakeLast(3).ShouldBe(["Authorized", "Capturing", "Captured"]);
    }

    [Fact]
    public async Task Nothing_is_captured_without_the_orders_request()
    {
        var attempt = await Attempt(Authorized());

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        _provider.Captures.ShouldBeEmpty();
        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorized);
    }

    [Fact]
    public async Task A_capture_that_timed_out_is_looked_up_and_sent_again_with_the_same_key_only_while_still_held()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        attempt.Status.ShouldBe(PaymentAttemptStatus.CaptureUnknown);

        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized); // not charged yet
        _provider.OnCapture = details => Ok(Snapshot(details.Reference, PaymentState.Captured) with { Captured = details.Amount });
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Captured);
        _provider.Captures.Select(c => c.Key.Value).Distinct().ShouldHaveSingleItem(); // the same key both times
    }

    [Fact]
    public async Task A_capture_found_done_is_recorded_without_charging_again()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        _provider.OnLookup = reference => Found(reference, PaymentState.Captured);
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Captured);
        _provider.Captures.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(PaymentState.Expired)] // F-24: the hold lapsed before the charge
    [InlineData(PaymentState.Canceled)]
    internal async Task F23_a_booked_order_that_cannot_be_charged_goes_to_a_person(PaymentState found)
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        _provider.OnLookup = reference => Found(reference, found);

        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
        _provider.Voids.ShouldBeEmpty(); // never released: the booking exists
    }

    [Fact]
    public async Task A_refused_capture_goes_to_a_person_and_is_not_repeated()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "refused")); // a definitive refusal

        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
        _provider.Captures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_capture_request_is_recorded_once_per_event_and_never_for_more_than_is_held_or_a_released_hold()
    {
        var attempt = await Attempt(Authorized());
        var request = Capture(attempt, _total with { Amount = 300m });
        await CaptureHandler().HandleAsync(request, Ct);
        attempt.CaptureRequestedAt.ShouldBeNull(); // more than held: never

        var released = await Attempt(Authorized(), Guid.NewGuid());
        await RequestRelease(released);
        await RequestCapture(released, _total);
        released.CaptureRequestedAt.ShouldBeNull(); // a hold being released is never charged

        var ok = Capture(attempt, _total);
        await CaptureHandler().HandleAsync(ok, Ct);
        await CaptureHandler().HandleAsync(ok, Ct);
        attempt.Events.Count(e => e.Reason.StartsWith("Capture requested", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_hold_with_a_capture_requested_is_never_released()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);

        attempt.RequestRelease("late release", new PaymentChange(_now, "test", null)).ShouldBeFalse();
        attempt.ReleaseRequestedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)] // a notification while the capture's outcome is unknown
    [InlineData(true)] // a notification while the capture is in flight (Capturing saved, provider not answered)
    public async Task B1_a_notification_during_a_capture_never_voids_the_hold_and_resumes_the_capture(bool interrupted)
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        if (interrupted)
        {
            attempt.BeginCapture(new PaymentChange(_now, "test", null));
        }
        else
        {
            _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));
            await Reconciler().ReconcileAsync(attempt.Id, Ct);
        }

        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized); // still only held at the provider
        _provider.OnCapture = details => Ok(Snapshot(details.Reference, PaymentState.Captured) with { Captured = details.Amount });
        await Reconciler().ReconcileNotifiedAsync(attempt.Id, null, Ct);

        _provider.Voids.ShouldBeEmpty();
        attempt.Status.ShouldBe(PaymentAttemptStatus.Captured);
    }

    [Fact]
    public async Task A_notification_about_a_captured_payment_changes_nothing()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = details => Ok(Snapshot(details.Reference, PaymentState.Captured) with { Captured = details.Amount });
        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        var events = attempt.Events.Count;

        await Reconciler().ReconcileNotifiedAsync(attempt.Id, null, Ct);

        (attempt.Events.Count, _provider.Voids.Count, _provider.Captures.Count).ShouldBe((events, 0, 1));
    }

    [Fact]
    public async Task A_capture_that_stays_unknown_goes_to_a_person_before_the_hold_can_lapse()
    {
        var attempt = await Attempt(Authorized());
        await RequestCapture(attempt, _total);
        _provider.OnCapture = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        _provider.OnLookupError = new ProviderError(ProviderErrorKind.Unavailable, "down");

        await Reconciler().ReconcileAsync(attempt.Id, Ct);
        attempt.Status.ShouldBe(PaymentAttemptStatus.CaptureUnknown);
        _clock.Advance(TimeSpan.FromHours(25));
        await Reconciler().ReconcileAsync(attempt.Id, Ct);

        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
        _provider.Voids.ShouldBeEmpty();
    }

    private Task RequestCapture(PaymentAttempt attempt, Money amount) => CaptureHandler().HandleAsync(Capture(attempt, amount), Ct);

    private static OrderPaymentCaptureRequested Capture(PaymentAttempt attempt, Money amount) => new(Guid.NewGuid(), _now, attempt.OrderId, attempt.Id, amount, "trace-7");

    private OrderPaymentCaptureRequestedHandler CaptureHandler() => new(_store, _clock, NullLogger<OrderPaymentCaptureRequestedHandler>.Instance);

    private async Task<PaymentAttempt> Attempt(Func<AuthorizationDetails, Result<PaymentSnapshot, ProviderError>> authorize, Guid? orderId = null)
    {
        _provider.OnAuthorize = authorize;
        var result = await Handler().AuthorizeAsync(new OrderPaymentRequest(orderId ?? _orderId, "cust-1", $"key-{Guid.NewGuid():N}", _total, "pm_test"), Ct);
        return _store.Attempts.Single(a => a.Id == result.Value.PaymentId);
    }

    private Task RequestRelease(PaymentAttempt attempt) => ReleaseHandler().HandleAsync(Release(attempt), Ct);

    private static OrderPaymentReleaseRequested Release(PaymentAttempt attempt) =>
        new(Guid.NewGuid(), _now, attempt.OrderId, attempt.Id, "the offer expired before booking", "trace-9");

    private static Func<AuthorizationDetails, Result<PaymentSnapshot, ProviderError>> Authorized() =>
        details => Ok(Snapshot(details.Reference, PaymentState.Authorized));

    private static Func<AuthorizationDetails, Result<PaymentSnapshot, ProviderError>> Unknown() =>
        _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));

    private AuthorizeOrderPaymentHandler Handler() => new(_store, Operations(), _clock, Options.Create(new PaymentReconciliationOptions()));

    private PaymentAttemptReconciler Reconciler() => new(_store, Handler(), Operations(), _clock, NullLogger<PaymentAttemptReconciler>.Instance);

    private OrderPaymentReleaseRequestedHandler ReleaseHandler() => new(_store, _clock);

    private ReconcilePaymentAttemptsJob Job()
    {
        var services = new ServiceCollection().AddScoped(_ => Reconciler()).BuildServiceProvider();
        return new ReconcilePaymentAttemptsJob(
            _store, services.GetRequiredService<IServiceScopeFactory>(), _clock, Options.Create(new PaymentReconciliationOptions()), NullLogger<ReconcilePaymentAttemptsJob>.Instance);
    }

    private PaymentOperations Operations() => new(_provider, _clock, NullLogger<PaymentOperations>.Instance);

    private static PaymentLookup Found(PaymentReference reference, PaymentState state) =>
        new(Snapshot(reference, state) with { Captured = state is PaymentState.Captured ? _total : _total with { Amount = 0m } });

    private static PaymentSnapshot Snapshot(PaymentReference reference, PaymentState state) =>
        new(reference, new ProviderPaymentRef("stub", $"pi_{reference.Value}"), state, _total, _total with { Amount = 0m }, _total with { Amount = 0m });

    private static Result<PaymentSnapshot, ProviderError> Ok(PaymentSnapshot payment) => Result<PaymentSnapshot, ProviderError>.Success(payment);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
