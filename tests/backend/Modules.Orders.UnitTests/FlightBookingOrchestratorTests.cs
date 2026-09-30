using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// Settling bookings whose outcome is unknown (booking-lifecycle.md, F-11, F-12, F-25): looked up by our reference and
/// never sent again; confirmed when found (then charged, once); failed only when not found after the supplier's
/// consistency window (then released); a person after the limit; never charged before a confirmed booking.
/// </summary>
public sealed class FlightBookingOrchestratorTests
{
    private static readonly Guid _paymentId = Guid.NewGuid();
    private readonly FakeTimeProvider _clock = new(OrderTests.Now.AddMinutes(1));
    private readonly StubBookings _bookings = new();
    private readonly FakeStore _store = new();
    private readonly Order _order = OrderTests.NewOrder();

    public FlightBookingOrchestratorTests()
    {
        _order.StartBooking(_paymentId.ToString(), new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"));
        _store.Orders.Add(_order);
    }

    private Guid ItemId => _order.Items[0].Id;

    [Fact]
    public async Task A_pending_booking_found_at_the_supplier_is_confirmed_and_charged_once_without_booking_again()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Booked, "mock", "LOC123", FlightTicketingStatus.Issued);

        await Reconcile();
        await Reconcile(); // nothing left to look up

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Confirmed);
        _order.Items[0].SupplierLocator.ShouldBe("LOC123");
        _bookings.Booked.ShouldBeEmpty();
        _bookings.LookedUp.ShouldBe([ItemId.ToString()]);
        var capture = _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentCaptureRequested>();
        (capture.PaymentId, capture.Amount).ShouldBe((_paymentId, OrderTests.Price));
    }

    [Fact]
    public async Task Not_found_inside_the_consistency_window_proves_nothing_and_settles_nothing()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.NotFound, "mock");
        _clock.Advance(TimeSpan.FromMinutes(10)); // under the 15-minute window

        await Reconcile();

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_found_after_the_consistency_window_fails_the_booking_and_releases_the_hold()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.NotFound, "mock");
        _clock.Advance(TimeSpan.FromMinutes(16));

        await Reconcile();

        _order.Status.ShouldBe(OrderStatus.Failed);
        _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentReleaseRequested>().PaymentId.ShouldBe(_paymentId);
        _bookings.Booked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_booking_still_unknown_after_the_limit_goes_to_a_person_neither_charged_nor_released()
    {
        Pending();
        _clock.Advance(TimeSpan.FromHours(25));

        await Reconcile();

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lookup_finding_a_booking_not_as_agreed_goes_to_a_person_and_is_never_charged()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Mismatch, "mock", "LOC999");

        await Reconcile();

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interrupted_booking_is_left_alone_while_it_may_still_be_in_flight_then_looked_up_never_sent_again()
    {
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Booked, "mock", "LOC123");
        _clock.Advance(TimeSpan.FromMinutes(4)); // the request that booked may still be waiting for the supplier

        (await Reconcile()).ShouldBeFalse();
        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Booking);

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reconcile()).ShouldBeTrue();

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Confirmed);
        _bookings.Booked.ShouldBeEmpty();
        _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentCaptureRequested>();
    }

    [Fact]
    public async Task A_booking_that_can_no_longer_be_sent_in_time_is_never_sent_and_its_hold_released()
    {
        _clock.Advance(TimeSpan.FromMinutes(3)); // the request stalled after moving the order to Booking

        await Orchestrator(new Travellers()).BookAsync(_order, new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"), TestContext.Current.CancellationToken);

        _bookings.Booked.ShouldBeEmpty();
        _order.Status.ShouldBe(OrderStatus.Failed);
        _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentReleaseRequested>();
    }

    [Fact]
    public async Task Lookups_that_settle_nothing_back_off_and_carry_a_correlation_id()
    {
        Pending();

        (await Reconcile()).ShouldBeTrue();
        (await Reconcile()).ShouldBeFalse(); // not due yet: backing off
        _clock.Advance(TimeSpan.FromSeconds(30));
        (await Reconcile()).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(30));
        (await Reconcile()).ShouldBeFalse(); // the next wait is a minute

        _bookings.LookedUp.Count.ShouldBe(2);
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Booked, "mock", "LOC123");
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Reconcile();
        _order.Timeline[^2].CorrelationId.ShouldNotBeNullOrEmpty(); // the confirmation and the charge note share it
        _order.Timeline[^1].CorrelationId.ShouldBe(_order.Timeline[^2].CorrelationId);
    }

    [Fact]
    public void The_payment_is_settled_once_and_never_while_a_booking_is_unsettled()
    {
        var context = new TransitionContext(_clock.GetUtcNow(), "test");
        _order.SettlePayment(context).ShouldBeNull(); // still booking

        _order.Confirm(ItemId, "mock", "LOC123", context);
        _order.SettlePayment(context).ShouldBe(new PaymentSettlement.Capture(_paymentId, OrderTests.Price));
        _order.SettlePayment(context).ShouldBeNull();
    }

    [Fact]
    public void A_failed_booking_is_released_never_charged()
    {
        var context = new TransitionContext(_clock.GetUtcNow(), "test");
        _order.Fail(ItemId, "refused", context);

        _order.SettlePayment(context).ShouldBe(new PaymentSettlement.Release(_paymentId));
    }

    private void Pending() =>
        _order.AwaitConfirmation(ItemId, "unknown", new TransitionContext(_clock.GetUtcNow(), "test")).IsSuccess.ShouldBeTrue();

    private Task<bool> Reconcile() => Orchestrator(new NoTravellers()).ReconcileAsync(_order.Id, TestContext.Current.CancellationToken);

    private FlightBookingOrchestrator Orchestrator(IOrderTravellers travellers) =>
        new(_store, _bookings, travellers, _clock, Options.Create(new BookingReconciliationOptions()), NullLogger<FlightBookingOrchestrator>.Instance);

    private sealed class Travellers : IOrderTravellers
    {
        public Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<BookingTravellers?>(new("ada@example.com", "+447700900123",
                [new BookingTraveller(TravellerType.Adult, "Ada", "Lovelace", new DateOnly(1990, 12, 10), TravellerGenderType.Female, null)]));
    }

    private sealed class NoTravellers : IOrderTravellers
    {
        public Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation never books.");
    }

    private sealed class FakeStore : IOrderStore
    {
        private readonly List<IIntegrationEvent> _pending = [];

        public List<Order> Orders { get; } = [];

        public List<IIntegrationEvent> Published { get; } = [];

        public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            Published.AddRange(_pending);
            _pending.Clear();
            return Task.FromResult(true);
        }

        public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
            where TEvent : IIntegrationEvent => _pending.Add(integrationEvent);

        public Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindBookingsToReconcileAsync(DateTimeOffset startedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
