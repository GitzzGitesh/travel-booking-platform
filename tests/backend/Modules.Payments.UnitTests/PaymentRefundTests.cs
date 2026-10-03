using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Refunds (ADR 0006, ADR 0027): recorded once per refund id and never beyond what was captured; sent once under our key;
/// an outcome that is unknown is looked up by that key and never sent again; still unknown after its limit, a person
/// decides. The final outcome goes to Orders and the customer's notice through the outbox.
/// </summary>
public sealed class PaymentRefundTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyCode _xts = new("XTS");
    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly ScriptedProvider _provider = new();
    private readonly PaymentAttempt _captured;

    public PaymentRefundTests()
    {
        _captured = PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-1", Money(300m), Change());
        _captured.Resolve(PaymentAttemptStatus.Authorized, "authorized", Change(), "stub", "pay_1");
        _captured.RequestCapture(Money(270m), Change());
        _captured.BeginCapture(Change());
        _captured.ResolveCapture(PaymentAttemptStatus.Captured, "captured", Change());
        _store.Attempts.Add(_captured);
    }

    [Fact]
    public async Task A_refund_within_what_was_captured_is_recorded_once_and_one_beyond_it_is_refused_and_reported()
    {
        var first = Requested(200m);
        await Handler().HandleAsync(first, Ct);
        await Handler().HandleAsync(first with { EventId = Guid.NewGuid() }, Ct); // the same refund id again
        await Handler().HandleAsync(Requested(80m), Ct); // 200 + 80 > 270 captured

        _store.Refunds.Count.ShouldBe(2);
        _store.Refunds[0].Status.ShouldBe(RefundRecordStatus.Requested);
        (_store.Refunds[1].Status, _store.Refunds[1].Reason).ShouldBe((RefundRecordStatus.Failed, "it would exceed what can still be refunded"));
        var reported = _store.Published.OfType<PaymentRefundSettled>().ShouldHaveSingleItem();
        (reported.RefundId, reported.Succeeded).ShouldBe((_store.Refunds[1].Id, false));
        _provider.RefundsSent.ShouldBeEmpty(); // the handler never calls the provider
    }

    [Fact]
    public async Task A_payment_that_was_not_captured_is_never_refunded()
    {
        var held = PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-2", Money(100m), Change());
        held.Resolve(PaymentAttemptStatus.Authorized, "authorized", Change(), "stub", "pay_2");
        _store.Attempts.Add(held);

        await Handler().HandleAsync(new OrderRefundRequested(Guid.NewGuid(), _now, held.OrderId, held.Id, Guid.NewGuid(), Money(50m), "trace-1"), Ct);

        _store.Refunds.ShouldHaveSingleItem().Status.ShouldBe(RefundRecordStatus.Failed);
    }

    [Fact]
    public async Task A_refund_is_sent_once_under_our_key_and_its_success_is_published()
    {
        var request = Requested(120m);
        await Handler().HandleAsync(request, Ct);
        _provider.OnRefund = details => Result<PaymentRefund, ProviderError>.Success(Refund(details.Key, RefundStatus.Succeeded));

        await Job().RunOnceAsync(Ct);
        await Job().RunOnceAsync(Ct); // nothing left to do

        var sent = _provider.RefundsSent.ShouldHaveSingleItem();
        (sent.Key.Value, sent.Amount).ShouldBe(($"{request.RefundId:N}:refund", Money(120m)));
        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.Succeeded);
        _store.Published.OfType<PaymentRefundSettled>().ShouldHaveSingleItem().Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_outcome_is_looked_up_never_sent_again_and_goes_to_a_person_if_it_stays_unknown()
    {
        await Handler().HandleAsync(Requested(120m), Ct);
        _provider.OnRefund = _ => Result<PaymentRefund, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "stub"));
        _provider.OnRefundLookup = _ => new RefundLookup(null); // not found (yet): proves nothing

        await Job().RunOnceAsync(Ct);
        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.Unknown);
        await Job().RunOnceAsync(Ct);
        _clock.Advance(RefundRecord.UnresolvedAfter);
        await Job().RunOnceAsync(Ct);

        _provider.RefundsSent.Count.ShouldBe(1); // never a second refund
        _provider.RefundLookups.ShouldBe(2);
        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.ManualReview);
        _store.Published.ShouldBeEmpty(); // not final: nothing told to Orders or the customer
    }

    [Fact]
    public async Task A_pending_refund_is_settled_by_a_lookup()
    {
        await Handler().HandleAsync(Requested(120m), Ct);
        _provider.OnRefund = details => Result<PaymentRefund, ProviderError>.Success(Refund(details.Key, RefundStatus.Pending));
        await Job().RunOnceAsync(Ct);
        _provider.OnRefundLookup = key => new RefundLookup(Refund(key, RefundStatus.Failed));

        await Job().RunOnceAsync(Ct);

        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.Failed);
        _store.Published.OfType<PaymentRefundSettled>().ShouldHaveSingleItem().Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_send_interrupted_after_it_was_saved_is_looked_up_not_sent_again()
    {
        await Handler().HandleAsync(Requested(120m), Ct);
        _store.Refunds.Single().BeginRefund(_clock.GetUtcNow()).ShouldBeTrue(); // saved as Refunding, then the process stopped
        _provider.OnRefundLookup = key => new RefundLookup(Refund(key, RefundStatus.Succeeded));

        await Job().RunOnceAsync(Ct); // too early: still trusted as in progress
        _clock.Advance(RefundRecord.InterruptedAfter);
        await Job().RunOnceAsync(Ct);

        _provider.RefundsSent.ShouldBeEmpty();
        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.Succeeded);
    }

    [Fact]
    public async Task An_invalid_refund_fails_and_gives_its_amount_back_and_a_key_for_other_details_goes_to_a_person()
    {
        await Handler().HandleAsync(Requested(270m), Ct);
        _captured.RefundedAmountValue.ShouldBe(270m); // reserved on the attempt in the same save
        _provider.OnRefund = _ => Result<PaymentRefund, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "stub"));

        await Job().RunOnceAsync(Ct);

        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.Failed);
        _captured.RefundedAmountValue.ShouldBe(0m); // nothing was refunded: it can be refunded again
        _captured.Events.ShouldContain(e => e.Reason.Contains("can be refunded again"));

        await Handler().HandleAsync(Requested(100m), Ct);
        _provider.OnRefund = _ => Result<PaymentRefund, ProviderError>.Failure(new ProviderError(ProviderErrorKind.IdempotencyConflict, "stub"));
        await Job().RunOnceAsync(Ct);
        _store.Refunds[1].Status.ShouldBe(RefundRecordStatus.ManualReview); // proves nothing either way: a person checks
        _captured.RefundedAmountValue.ShouldBe(100m); // still held until a person settles it
    }

    [Fact]
    public async Task A_refund_still_pending_after_its_limit_goes_to_a_person()
    {
        await Handler().HandleAsync(Requested(120m), Ct);
        _provider.OnRefund = details => Result<PaymentRefund, ProviderError>.Success(Refund(details.Key, RefundStatus.Pending));
        await Job().RunOnceAsync(Ct);
        _provider.OnRefundLookup = key => new RefundLookup(Refund(key, RefundStatus.Pending));

        _clock.Advance(RefundRecord.UnresolvedAfter);
        await Job().RunOnceAsync(Ct);

        _store.Refunds.Single().Status.ShouldBe(RefundRecordStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Money Money(decimal amount) => new(amount, _xts);

    private OrderRefundRequested Requested(decimal amount) =>
        new(Guid.NewGuid(), _now, _captured.OrderId, _captured.Id, Guid.NewGuid(), Money(amount), "trace-1");

    private PaymentRefund Refund(OperationKey key, RefundStatus status) =>
        new(new PaymentReference(_captured.Reference), key, new ProviderRefundRef("stub", $"re_{key.Value}"),
            _store.Refunds.Single(r => r.Key == key.Value).Amount, status);

    private OrderRefundRequestedHandler Handler() => new(_store, _clock, NullLogger<OrderRefundRequestedHandler>.Instance);

    private ExecuteRefundsJob Job()
    {
        var services = new ServiceCollection()
            .AddSingleton<IPaymentAttemptStore>(_store)
            .AddSingleton<TimeProvider>(_clock)
            .AddSingleton(new PaymentOperations(_provider, _clock, NullLogger<PaymentOperations>.Instance))
            .BuildServiceProvider();
        return new ExecuteRefundsJob(_store, services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<ExecuteRefundsJob>.Instance);
    }

    private PaymentChange Change() => new(_clock.GetUtcNow(), "test", "trace-1");
}
