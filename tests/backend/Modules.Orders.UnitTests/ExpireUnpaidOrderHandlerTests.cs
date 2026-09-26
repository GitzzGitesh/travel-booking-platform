using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// Offer expiry before payment (F-02, booking rules): an order is abandoned only when no payment attempt for it holds, or
/// may hold, funds. A hold it will not use is released first (one request, through the outbox), and every step is
/// idempotent.
/// </summary>
public sealed class ExpireUnpaidOrderHandlerTests
{
    private readonly FakeTimeProvider _clock = new(OrderTests.Now.AddHours(1)); // past the offer's expiry (Now + 30 minutes)
    private readonly FakeStore _store = new();
    private readonly StubPayments _payments = new();
    private readonly Order _order = OrderTests.NewOrder();

    public ExpireUnpaidOrderHandlerTests() => _store.Orders.Add(_order);

    [Fact]
    public async Task An_expired_order_with_no_live_payment_is_abandoned_once()
    {
        (await Handle()).ShouldBe(ExpiryOutcome.Abandoned);
        var entries = _order.Timeline.Count;
        (await Handle()).ShouldBe(ExpiryOutcome.NothingToDo);

        _order.Status.ShouldBe(OrderStatus.Abandoned);
        _order.Timeline.Count.ShouldBe(entries);
        var entry = _order.Timeline[^1];
        (entry.FromStatus, entry.ToStatus, entry.Actor).ShouldBe(("AwaitingPayment", "Abandoned", ExpireUnpaidOrderHandler.Actor));
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_order_whose_offer_is_still_valid_is_left_alone()
    {
        var beforeExpiry = new FakeTimeProvider(OrderTests.Now.AddMinutes(29));

        (await new ExpireUnpaidOrderHandler(_store, _payments, beforeExpiry).HandleAsync(_order.Id, TestContext.Current.CancellationToken))
            .ShouldBe(ExpiryOutcome.NothingToDo);

        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        _payments.Queries.ShouldBe(0);
    }

    [Theory]
    [InlineData(OrderPaymentStatus.Authorized)]
    [InlineData(OrderPaymentStatus.ActionRequired)] // an unfinished challenge: canceled by the release
    internal async Task An_expired_order_holding_funds_asks_for_the_release_once_and_is_not_abandoned(OrderPaymentStatus status)
    {
        _payments.Live = new LiveOrderPayment(Guid.NewGuid(), status, ReleaseRequested: false);

        (await Handle()).ShouldBe(ExpiryOutcome.ReleaseRequested);
        (await Handle()).ShouldBe(ExpiryOutcome.ReleaseRequested); // the event is not consumed yet: still no second request

        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        var request = _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentReleaseRequested>();
        (request.OrderId, request.PaymentId, request.Reason).ShouldBe((_order.Id, _payments.Live.PaymentId, ExpireUnpaidOrderHandler.Reason));
        _order.Timeline[^1].ProviderReference.ShouldBe(_payments.Live.PaymentId.ToString());
    }

    [Theory]
    [InlineData(OrderPaymentStatus.Pending, false)] // an outcome or a void still unknown
    [InlineData(OrderPaymentStatus.ManualReview, false)]
    [InlineData(OrderPaymentStatus.Authorized, true)] // release already requested: being voided
    internal async Task An_expired_order_waits_while_its_payment_is_unsettled(OrderPaymentStatus status, bool releaseRequested)
    {
        _payments.Live = new LiveOrderPayment(Guid.NewGuid(), status, releaseRequested);
        var entries = _order.Timeline.Count;

        (await Handle()).ShouldBe(ExpiryOutcome.WaitingForPayment);

        (_order.Status, _order.Timeline.Count, _store.Saves).ShouldBe((OrderStatus.AwaitingPayment, entries, 0));
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_release_request_the_outbox_gave_up_on_is_published_again()
    {
        _payments.Live = new LiveOrderPayment(Guid.NewGuid(), OrderPaymentStatus.Authorized, ReleaseRequested: false);
        await Handle();
        _store.DispatchGaveUp = true; // Payments never recorded it

        (await Handle()).ShouldBe(ExpiryOutcome.ReleaseRequested);

        _store.Published.Count.ShouldBe(2);
        _store.Published.Cast<OrderPaymentReleaseRequested>().Select(e => e.EventId).Distinct().Count().ShouldBe(2);
        _order.Timeline.Count(e => e.ProviderReference == _payments.Live.PaymentId.ToString()).ShouldBe(1); // noted once
    }

    [Fact]
    public async Task Once_the_hold_is_released_the_order_is_abandoned()
    {
        _payments.Live = new LiveOrderPayment(Guid.NewGuid(), OrderPaymentStatus.Authorized, ReleaseRequested: false);
        await Handle();
        _payments.Live = null; // Payments voided it

        (await Handle()).ShouldBe(ExpiryOutcome.Abandoned);

        _order.Status.ShouldBe(OrderStatus.Abandoned);
    }

    [Fact]
    public async Task A_concurrent_change_leaves_the_order_for_the_next_run()
    {
        _store.SaveSucceeds = false;

        (await Handle()).ShouldBe(ExpiryOutcome.Conflict);
    }

    [Fact]
    public async Task An_unknown_order_is_nothing_to_do() =>
        (await new ExpireUnpaidOrderHandler(_store, _payments, _clock).HandleAsync(Guid.NewGuid(), TestContext.Current.CancellationToken))
            .ShouldBe(ExpiryOutcome.NothingToDo);

    private Task<ExpiryOutcome> Handle() =>
        new ExpireUnpaidOrderHandler(_store, _payments, _clock).HandleAsync(_order.Id, TestContext.Current.CancellationToken);

    private sealed class StubPayments : IOrderPayments
    {
        public LiveOrderPayment? Live { get; set; }

        public int Queries { get; private set; }

        public Task<LiveOrderPayment?> FindLiveAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Queries++;
            return Task.FromResult(Live);
        }

        public Task<Result<OrderPaymentResult, OrderPaymentFailure>> AuthorizeAsync(OrderPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Expiry never authorizes.");

        public Task<OrderPaymentResult?> ResumeAsync(Guid orderId, string customerId, string idempotencyKey, string? correlationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Expiry never looks a payment up.");
    }

    private sealed class FakeStore : IOrderStore
    {
        public List<Order> Orders { get; } = [];

        public List<IIntegrationEvent> Published { get; } = [];

        public bool SaveSucceeds { get; set; } = true;

        public int Saves { get; private set; }

        public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(SaveSucceeds);
        }

        public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
            where TEvent : IIntegrationEvent => Published.Add(integrationEvent);

        public Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <summary>The outbox gave up dispatching the published requests (e.g. Payments was down for a long time).</summary>
        public bool DispatchGaveUp { get; set; }

        public Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken) =>
            Task.FromResult(!DispatchGaveUp && Published.OfType<OrderPaymentReleaseRequested>().Any(e => e.PaymentId == paymentId));
    }
}
