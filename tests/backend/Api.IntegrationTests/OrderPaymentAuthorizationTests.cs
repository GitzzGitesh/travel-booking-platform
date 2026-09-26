using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Persistent payment attempts through the composed Api, the mock provider and a real SQL Server (payment-lifecycle.md;
/// F-20, F-21, F-32): idempotency is enforced by the database, and unknown outcomes are looked up, never re-authorized.
/// Other modules reach this only through Payments.Contracts. No endpoint exists (ADR 0006 is Proposed).
/// </summary>
public sealed class OrderPaymentAuthorizationTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _customer = "test-customer-1";
    private static readonly Money _total = new(540m, new CurrencyCode("XTS"));

    [Fact]
    public async Task An_approved_payment_is_authorized_and_persisted_with_its_history()
    {
        var request = Request(MockPaymentMethods.Approved);

        var result = (await Authorize(request)).Value;

        result.Status.ShouldBe(OrderPaymentStatus.Authorized);
        var stored = await Load(result.PaymentId);
        (stored.OrderId, stored.CustomerId, stored.Amount, stored.Status).ShouldBe((request.OrderId, _customer, _total, PaymentAttemptStatus.Authorized));
        stored.ProviderId.ShouldBe("mockpay");
        stored.ProviderPaymentId.ShouldNotBeNullOrEmpty();
        stored.Events.OrderBy(e => e.Id).Select(e => e.ToStatus).ShouldBe(["Authorizing", "Authorized"]);
    }

    [Fact]
    public async Task F20_a_decline_is_final_and_replaying_the_key_returns_the_same_attempt()
    {
        var request = Request(MockPaymentMethods.InsufficientFunds);

        var first = (await Authorize(request)).Value;
        var replay = (await Authorize(request)).Value;

        first.ShouldBe(new OrderPaymentResult(first.PaymentId, OrderPaymentStatus.Declined, "InsufficientFunds"));
        replay.ShouldBe(first);
        (await Load(first.PaymentId)).Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task F21_a_challenge_returns_the_customer_action()
    {
        var result = (await Authorize(Request(MockPaymentMethods.RequiresAction))).Value;

        result.Status.ShouldBe(OrderPaymentStatus.ActionRequired);
        result.CustomerAction.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_authorization_that_timed_out_but_was_made_is_found_by_lookup_on_the_replay()
    {
        var request = Request(MockPaymentMethods.TimeoutAuthorized);

        var first = (await Authorize(request)).Value;
        var replay = (await Authorize(request)).Value;

        first.Status.ShouldBe(OrderPaymentStatus.Pending);
        replay.ShouldBe(first with { Status = OrderPaymentStatus.Authorized });
        (await Load(first.PaymentId)).Events.OrderBy(e => e.Id).Select(e => e.ToStatus).ShouldBe(["Authorizing", "AuthorizationUnknown", "Authorized"]);
    }

    [Fact]
    public async Task An_authorization_that_was_never_made_fails_only_after_the_consistency_window()
    {
        var request = Request(MockPaymentMethods.TimeoutNotAuthorized);
        (await Authorize(request)).Value.Status.ShouldBe(OrderPaymentStatus.Pending);

        var early = (await Authorize(request)).Value;
        api.Clock.Advance(TimeSpan.FromMinutes(15));
        var late = (await Authorize(request)).Value;

        early.Status.ShouldBe(OrderPaymentStatus.Pending);
        late.Status.ShouldBe(OrderPaymentStatus.Failed);
    }

    [Fact]
    public async Task Reusing_a_key_for_another_amount_is_refused()
    {
        var request = Request(MockPaymentMethods.Approved);
        await Authorize(request);

        (await Authorize(request with { Amount = _total with { Amount = 541m } })).Error.ShouldBe(OrderPaymentFailure.IdempotencyKeyReused);
    }

    [Fact]
    public async Task F32_a_second_key_cannot_hold_funds_while_the_first_attempt_may_and_can_after_a_decline()
    {
        var orderId = Guid.NewGuid();
        var pending = await Authorize(Request(MockPaymentMethods.TimeoutAuthorized) with { OrderId = orderId });
        var second = await Authorize(Request(MockPaymentMethods.Approved) with { OrderId = orderId });
        var declinedOrder = Guid.NewGuid();
        await Authorize(Request(MockPaymentMethods.Declined) with { OrderId = declinedOrder });
        var afterDecline = await Authorize(Request(MockPaymentMethods.Approved) with { OrderId = declinedOrder });

        pending.Value.Status.ShouldBe(OrderPaymentStatus.Pending);
        second.Error.ShouldBe(OrderPaymentFailure.PaymentInProgress);
        afterDecline.Value.Status.ShouldBe(OrderPaymentStatus.Authorized);
    }

    [Fact]
    public async Task Parallel_requests_with_one_key_create_exactly_one_attempt()
    {
        var request = Request(MockPaymentMethods.Approved);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Authorize(request)));

        results.Select(r => r.Value.PaymentId).Distinct().ShouldHaveSingleItem();
        using var scope = api.Services.CreateScope();
        var attempts = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts
            .Where(a => a.OrderId == request.OrderId).ToListAsync(Ct);
        attempts.ShouldHaveSingleItem().Status.ShouldBe(PaymentAttemptStatus.Authorized);
    }

    private static OrderPaymentRequest Request(string token) => new(Guid.NewGuid(), _customer, $"pay-{Guid.NewGuid():N}", _total, token);

    private async Task<Result<OrderPaymentResult, OrderPaymentFailure>> Authorize(OrderPaymentRequest request)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderPayments>().AuthorizeAsync(request, Ct);
    }

    private async Task<PaymentAttempt> Load(Guid id)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking()
            .Include(a => a.Events).SingleAsync(a => a.Id == id, Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
