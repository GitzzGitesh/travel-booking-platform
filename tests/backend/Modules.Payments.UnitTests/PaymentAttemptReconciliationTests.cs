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
    public async Task The_live_attempt_is_reported_to_orders_with_its_release_state()
    {
        var attempt = await Attempt(Authorized());
        await RequestRelease(attempt);

        var live = await Handler().FindLiveAsync(_orderId, Ct);

        live.ShouldBe(new LiveOrderPayment(attempt.Id, OrderPaymentStatus.Pending, ReleaseRequested: true));
        (await Handler().FindLiveAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

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

    private PaymentAttemptReconciler Reconciler() => new(_store, Handler(), Operations(), _clock);

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
