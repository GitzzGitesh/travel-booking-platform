using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Orders.Infrastructure;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The Worker's background processing against a real SQL Server (ADR 0007): job leases, the Orders outbox and the
/// Payments inbox, payment-hold release and order offer expiry, working together through the mock payment provider.
/// The jobs are run explicitly; the scheduler is never started in tests.
/// </summary>
public sealed class BackgroundProcessingTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _customer = "test-customer-1";
    private static readonly Money _price = new(540m, new CurrencyCode("XTS"));

    [Fact]
    public async Task A_lease_is_held_by_one_owner_until_it_expires_or_is_released()
    {
        var job = $"test.lease-{Guid.NewGuid():N}";
        using var scope = api.Services.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<EfJobLeaseStore<OrdersDbContext>>();

        (await leases.TryAcquireAsync(job, "worker-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        (await leases.TryAcquireAsync(job, "worker-b", TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
        (await leases.TryAcquireAsync(job, "worker-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue(); // renewal

        api.Clock.Advance(TimeSpan.FromMinutes(6)); // worker-a died holding it
        (await leases.TryAcquireAsync(job, "worker-b", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        await leases.ReleaseAsync(job, "worker-a", Ct); // a stale owner cannot release it
        (await leases.TryAcquireAsync(job, "worker-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();

        await leases.ReleaseAsync(job, "worker-b", Ct);
        (await leases.TryAcquireAsync(job, "worker-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_worker_does_not_run_a_job_another_worker_holds_and_takes_it_over_after_a_crash()
    {
        var schedule = Schedule(ExpireUnpaidOrdersJob.Name);
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EfJobLeaseStore<OrdersDbContext>>()
                .TryAcquireAsync(schedule.Name, "crashed-worker", schedule.LeaseDuration, Ct);
        }

        var runner = api.Services.GetRequiredService<BackgroundJobRunner>();
        (await runner.RunOnceAsync(schedule, Ct)).ShouldBeFalse();

        api.Clock.Advance(schedule.LeaseDuration);
        (await runner.RunOnceAsync(schedule, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task F22_an_unused_hold_on_an_expired_order_is_released_before_the_order_is_abandoned()
    {
        var order = await NewOrder();
        var payment = await Authorize(order, MockPaymentMethods.Approved);
        api.Clock.Advance(TimeSpan.FromMinutes(31)); // the offer expired before booking

        (await Expire(order.Id)).ShouldBe(ExpiryOutcome.ReleaseRequested);
        (await LoadOrder(order.Id)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await Run(_ordersOutbox)).ShouldBeGreaterThanOrEqualTo(1);
        (await LoadPayment(payment)).ReleaseRequestedAt.ShouldNotBeNull();

        await Run(_paymentsReconciliation);
        (await LoadPayment(payment)).Status.ShouldBe(PaymentAttemptStatus.Voided);
        (await Expire(order.Id)).ShouldBe(ExpiryOutcome.Abandoned);

        var stored = await LoadOrder(order.Id);
        stored.Status.ShouldBe(OrderStatus.Abandoned);
        stored.Timeline.Select(e => e.ToStatus).ShouldBe(["Draft", "AwaitingPayment", "AwaitingPayment", "Abandoned"]);
        stored.Timeline[2].ProviderReference.ShouldBe(payment.ToString());
        (await LoadPayment(payment)).Events.Select(e => e.ToStatus).ShouldBe(["Authorizing", "Authorized", "Authorized", "Voiding", "Voided"]);
    }

    [Fact]
    public async Task Repeating_every_job_after_the_release_changes_nothing()
    {
        var order = await NewOrder();
        var payment = await Authorize(order, MockPaymentMethods.Approved);
        api.Clock.Advance(TimeSpan.FromMinutes(31));
        await Expire(order.Id);
        await Run(_ordersOutbox);
        await Run(_paymentsReconciliation);
        await Expire(order.Id);
        var (orderEntries, paymentEvents) = ((await LoadOrder(order.Id)).Timeline.Count, (await LoadPayment(payment)).Events.Count);

        await Expire(order.Id);
        await Run(_ordersOutbox);
        await Run(_paymentsReconciliation);

        ((await LoadOrder(order.Id)).Timeline.Count, (await LoadPayment(payment)).Events.Count).ShouldBe((orderEntries, paymentEvents));
    }

    [Fact]
    public async Task A_redelivered_release_request_is_recorded_once_by_the_inbox()
    {
        var order = await NewOrder();
        var payment = await Authorize(order, MockPaymentMethods.Approved);
        api.Clock.Advance(TimeSpan.FromMinutes(31));
        await Expire(order.Id);
        await Run(_ordersOutbox);

        // The dispatcher crashed after the handler succeeded but before marking the message processed.
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<OutboxMessage>()
                .Where(m => m.Payload.Contains(order.Id.ToString()))
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null), Ct);
        }

        await Run(_ordersOutbox);

        (await LoadPayment(payment)).Events.Count(e => e.Reason.StartsWith("Release requested", StringComparison.Ordinal)).ShouldBe(1);
        using var check = api.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<OutboxMessage>()
            .SingleAsync(m => m.Payload.Contains(order.Id.ToString()), Ct)).ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_expired_order_with_an_unknown_payment_waits_until_the_lookup_settles_it()
    {
        var order = await NewOrder();
        var payment = await Authorize(order, MockPaymentMethods.TimeoutNotAuthorized);
        api.Clock.Advance(TimeSpan.FromMinutes(31));

        (await Expire(order.Id)).ShouldBe(ExpiryOutcome.WaitingForPayment);
        await Run(_paymentsReconciliation); // not found, but after the 15-minute consistency window: nothing was held
        (await LoadPayment(payment)).Status.ShouldBe(PaymentAttemptStatus.Failed);
        (await Expire(order.Id)).ShouldBe(ExpiryOutcome.Abandoned);
    }

    [Fact]
    public async Task An_expired_order_with_no_payment_is_abandoned_once_even_by_two_workers_at_the_same_time()
    {
        var order = await NewOrder();
        api.Clock.Advance(TimeSpan.FromMinutes(31));

        await Task.WhenAll(Run(ExpireUnpaidOrdersJob.Name), Run(ExpireUnpaidOrdersJob.Name));

        (await LoadOrder(order.Id)).Timeline.Count(e => e.ToStatus == "Abandoned").ShouldBe(1);
    }

    [Fact]
    public async Task A_message_no_handler_accepts_backs_off_instead_of_blocking_the_outbox()
    {
        var unknown = new UnhandledTestEvent(Guid.NewGuid(), api.Clock.GetUtcNow());
        using (var scope = api.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOrderStore>();
            store.Publish(unknown, "test-trace");
            (await store.TrySaveAsync(Ct)).ShouldBeTrue();
        }

        await Run(_ordersOutbox);
        var failed = await LoadMessage(unknown.EventId);
        (failed.ProcessedAt, failed.Attempts, failed.LastError).ShouldBe((null, 1, nameof(InvalidOperationException)));
        failed.NextAttemptAt.ShouldBeGreaterThan(api.Clock.GetUtcNow());

        await Run(_ordersOutbox); // before its next attempt: left alone
        (await LoadMessage(unknown.EventId)).Attempts.ShouldBe(1);
    }

    private const string _ordersOutbox = "orders.outbox";
    private const string _paymentsReconciliation = "payments.reconcile-attempts";

    private async Task<Order> NewOrder()
    {
        using var scope = api.Services.CreateScope();
        var now = api.Clock.GetUtcNow();
        var order = Order.CreateForFlight(_customer, $"order-{Guid.NewGuid():N}", Guid.NewGuid(), _price, now.AddMinutes(30), null,
            new TransitionContext(now, CreateFlightOrderHandler.Actor(_customer), "test-trace"));
        (await scope.ServiceProvider.GetRequiredService<IOrderStore>().TryAddAsync(order, Ct)).ShouldBeTrue();
        return order;
    }

    private async Task<Guid> Authorize(Order order, string token)
    {
        using var scope = api.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IOrderPayments>()
            .AuthorizeAsync(new OrderPaymentRequest(order.Id, _customer, $"pay-{Guid.NewGuid():N}", order.Total, token, "test-trace"), Ct);
        return result.Value.PaymentId;
    }

    private async Task<ExpiryOutcome> Expire(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExpireUnpaidOrderHandler>().HandleAsync(orderId, Ct);
    }

    /// <summary>Runs one job directly (no lease): what a Worker does once it holds the lease.</summary>
    private async Task<int> Run(string job)
    {
        using var scope = api.Services.CreateScope();
        return await ((IBackgroundJob)scope.ServiceProvider.GetRequiredService(Schedule(job).JobType)).RunOnceAsync(Ct);
    }

    private BackgroundJobSchedule Schedule(string job) =>
        api.Services.GetServices<BackgroundJobSchedule>().Single(s => s.Name == job);

    private async Task<Order> LoadOrder(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IOrderStore>().FindAsync(orderId, Ct))!;
    }

    private async Task<PaymentAttempt> LoadPayment(Guid paymentId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking()
            .Include(a => a.Events).SingleAsync(a => a.Id == paymentId, Ct);
    }

    private async Task<OutboxMessage> LoadMessage(Guid id)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record UnhandledTestEvent(Guid EventId, DateTimeOffset OccurredAt) : IIntegrationEvent;
}
