using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// Settling bookings whose outcome is unknown (booking-lifecycle.md, F-11, F-12, F-25): looked up by our reference and
/// never sent again; confirmed when found (then charged, once); failed only when not found after the supplier's
/// consistency window (then released); a person after the limit; never charged before a confirmed booking.
/// </summary>
public sealed class BookingOrchestratorTests
{
    private static readonly Guid _paymentId = Guid.NewGuid();
    private readonly FakeTimeProvider _clock = new(OrderTests.Now.AddMinutes(1));
    private readonly StubBookings _bookings = new();
    private readonly StubHotelBookings _hotelBookings = new();
    private readonly FakeStore _store = new();
    private readonly Order _order = OrderTests.NewOrder();

    public BookingOrchestratorTests()
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

        _order.Items[0].Status.ShouldBe(OrderItemStatus.Confirmed);
        _order.Items[0].SupplierLocator.ShouldBe("LOC123");
        _bookings.Booked.ShouldBeEmpty();
        _bookings.LookedUp.ShouldBe([ItemId.ToString()]);
        var capture = _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem();
        (capture.PaymentId, capture.Amount).ShouldBe((_paymentId, OrderTests.Price));
    }

    [Fact]
    public async Task Not_found_inside_the_consistency_window_proves_nothing_and_settles_nothing()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.NotFound, "mock");
        _clock.Advance(TimeSpan.FromMinutes(10)); // under the 15-minute window

        await Reconcile();

        _order.Items[0].Status.ShouldBe(OrderItemStatus.PendingConfirmation);
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
        _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem().PaymentId.ShouldBe(_paymentId);
        _bookings.Booked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_booking_still_unknown_after_the_limit_goes_to_a_person_neither_charged_nor_released()
    {
        Pending();
        _clock.Advance(TimeSpan.FromHours(25));

        await Reconcile();

        _order.Items[0].Status.ShouldBe(OrderItemStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lookup_finding_a_booking_not_as_agreed_goes_to_a_person_and_is_never_charged()
    {
        Pending();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Mismatch, "mock", "LOC999");

        await Reconcile();

        _order.Items[0].Status.ShouldBe(OrderItemStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interrupted_booking_is_left_alone_while_it_may_still_be_in_flight_then_looked_up_never_sent_again()
    {
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Booked, "mock", "LOC123");
        _clock.Advance(TimeSpan.FromMinutes(4)); // the request that booked may still be waiting for the supplier

        (await Reconcile()).ShouldBeFalse();
        _order.Items[0].Status.ShouldBe(OrderItemStatus.Booking);

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reconcile()).ShouldBeTrue();

        _order.Items[0].Status.ShouldBe(OrderItemStatus.Confirmed);
        _bookings.Booked.ShouldBeEmpty();
        _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_booking_that_can_no_longer_be_sent_in_time_is_never_sent_and_its_hold_released()
    {
        _clock.Advance(TimeSpan.FromMinutes(3)); // the request stalled after moving the order to Booking

        await Orchestrator(new Travellers()).BookAsync(_order, new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"), TestContext.Current.CancellationToken);

        _bookings.Booked.ShouldBeEmpty();
        _order.Status.ShouldBe(OrderStatus.Failed);
        _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem();
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

    // ---------- A staff member's review check (ADR 0022) ----------

    [Fact]
    public async Task A_review_check_finding_the_booking_as_agreed_confirms_and_charges()
    {
        InReview();
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.Booked, "mock", "LOC123", FlightTicketingStatus.Issued);

        (await CheckReview()).ShouldBe(OrderItemStatus.Confirmed);

        _order.Items[0].Ticketing.ShouldBe(TicketingStatus.Issued);
        _store.Pending.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem();
        _store.Pending.OfType<OrderBookingSettled>().ShouldHaveSingleItem().Outcome.ShouldBe(BookingOutcome.Confirmed);
        _bookings.Booked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(FlightBookingStatus.Mismatch)] // a booking not as agreed is never confirmed or charged by a check
    [InlineData(FlightBookingStatus.Unknown)]
    [InlineData(FlightBookingStatus.NotBooked)]
    internal async Task A_review_check_that_proves_nothing_leaves_the_booking_in_review_and_settles_nothing(FlightBookingStatus found)
    {
        InReview();
        _bookings.NextLookup = new FlightBookingResult(found, "mock", "LOC999");
        var entries = _order.Timeline.Count;

        (await CheckReview()).ShouldBe(OrderItemStatus.ManualReview);

        _store.Pending.ShouldBeEmpty();
        _order.Timeline.Count.ShouldBe(entries + 1); // the check is on the timeline
        _order.Timeline[^1].Reason.ShouldContain("TICKET-1");
    }

    [Fact]
    public async Task A_booking_once_found_not_as_agreed_is_never_failed_by_a_later_not_found()
    {
        _order.AwaitConfirmation(ItemId, "unknown", new TransitionContext(_clock.GetUtcNow(), "test"));
        _order.RequireManualReview(ItemId, "not as agreed", new TransitionContext(_clock.GetUtcNow(), "test"), providerReference: "mock:LOC999");
        _clock.Advance(TimeSpan.FromHours(2));
        _bookings.NextLookup = new FlightBookingResult(FlightBookingStatus.NotFound, "mock");

        (await CheckReview()).ShouldBe(OrderItemStatus.ManualReview);

        _store.Pending.ShouldBeEmpty(); // never released: a ticket may exist
    }

    [Fact]
    public async Task A_review_check_whose_lookup_fails_leaves_the_booking_in_review()
    {
        InReview();
        _bookings.LookupThrows = true;

        (await CheckReview()).ShouldBe(OrderItemStatus.ManualReview);

        _store.Pending.ShouldBeEmpty();
    }

    [Fact]
    public void A_review_note_is_only_added_to_an_item_in_review()
    {
        var entries = _order.Timeline.Count;

        _order.NoteReviewCheck(ItemId, "checked", new TransitionContext(_clock.GetUtcNow(), "test")); // still Booking

        _order.Timeline.Count.ShouldBe(entries);
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

    [Fact]
    public async Task A_hotel_item_is_booked_through_hotels_with_the_lead_adult_first_and_no_tickets()
    {
        var order = HotelOrder();

        await Orchestrator(new Guests()).BookAsync(order, new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"), TestContext.Current.CancellationToken);

        _bookings.Booked.ShouldBeEmpty(); // never Flights for a hotel stay
        var request = _hotelBookings.Booked.ShouldHaveSingleItem();
        request.ClientReference.ShouldBe(order.Items[0].Id.ToString());
        request.AgreedPrice.ShouldBe(OrderTests.Price);
        request.Guests.Select(g => (g.Surname, g.ChildAge)).ShouldBe([("Lovelace", (int?)null), ("Byron", null), ("Lovelace", 7), ("Byron", 14)]);
        var item = order.Items[0];
        (item.Status, item.SupplierLocator, item.Ticketing).ShouldBe((OrderItemStatus.Confirmed, "MH123", (TicketingStatus?)null));
        _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem().Amount.ShouldBe(OrderTests.Price);
    }

    [Fact]
    public async Task A_pending_hotel_booking_is_looked_up_through_hotels_and_a_mismatch_is_never_charged()
    {
        var order = HotelOrder();
        order.AwaitConfirmation(order.Items[0].Id, "unknown", new TransitionContext(_clock.GetUtcNow(), "test")).IsSuccess.ShouldBeTrue();
        _hotelBookings.NextLookup = new HotelBookingResult(HotelBookingStatus.Mismatch, "mockhotels", "MH999");

        await Orchestrator(new NoTravellers()).ReconcileAsync(order.Id, TestContext.Current.CancellationToken);

        _hotelBookings.LookedUp.ShouldBe([order.Items[0].Id.ToString()]);
        (_bookings.LookedUp.Count, _hotelBookings.Booked.Count).ShouldBe((0, 0)); // looked up with Hotels only, never booked again
        order.Items[0].Status.ShouldBe(OrderItemStatus.ManualReview);
        _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pending_hotel_booking_found_is_confirmed_and_charged()
    {
        var order = HotelOrder();
        order.AwaitConfirmation(order.Items[0].Id, "unknown", new TransitionContext(_clock.GetUtcNow(), "test")).IsSuccess.ShouldBeTrue();
        _hotelBookings.NextLookup = new HotelBookingResult(HotelBookingStatus.Booked, "mockhotels", "MH123");

        await Orchestrator(new NoTravellers()).ReconcileAsync(order.Id, TestContext.Current.CancellationToken);

        (order.Items[0].Status, order.Items[0].SupplierLocator).ShouldBe((OrderItemStatus.Confirmed, "MH123"));
        _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Each_booking_outcome_is_counted_once_when_saved_with_its_product_and_no_ids()
    {
        var measured = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, BookingOrchestrator.BookingOutcomes))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (measured)
            {
                measured.Add((value, copy));
            }
        });
        listener.Start();
        var order = HotelOrder();

        await Orchestrator(new Guests()).BookAsync(order, new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"), TestContext.Current.CancellationToken);
        await Orchestrator(new NoTravellers()).ReconcileAsync(order.Id, TestContext.Current.CancellationToken); // nothing left to change

        var mine = measured.Where(m => Equals(m.Tags.GetValueOrDefault("product"), "Hotel")).ToList();
        var outcome = mine.ShouldHaveSingleItem();
        outcome.Value.ShouldBe(1);
        outcome.Tags.ShouldBe(new Dictionary<string, object?> { ["product"] = "Hotel", ["outcome"] = "Confirmed" });
    }

    private Order HotelOrder()
    {
        var order = Order.CreateForHotel("cust-1", "key-h", Guid.NewGuid(), OrderTests.Price, OrderTests.Now.AddMinutes(30), null,
            new TransitionContext(OrderTests.Now, "customer", "trace-h"), new TravellerNeeds(3, 1, 0, false, new DateOnly(2027, 4, 13)), CancellationTerms.NonRefundable);
        order.Items[0].Product.ShouldBe(OrderProduct.Hotel);
        order.Timeline[0].Reason.ShouldBe("Order created from a confirmed hotel selection");
        order.StartBooking(_paymentId.ToString(), new TransitionContext(_clock.GetUtcNow(), "customer:cust-1"));
        _store.Orders.Add(order);
        return order;
    }

    private void InReview()
    {
        Pending();
        _order.RequireManualReview(ItemId, "still unknown after its limit", new TransitionContext(_clock.GetUtcNow(), "test")).IsSuccess.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromHours(25));
    }

    private Task<OrderItemStatus> CheckReview() =>
        Orchestrator(new NoTravellers()).CheckReviewAsync(_order, ItemId, "TICKET-1", new TransitionContext(_clock.GetUtcNow(), "staff:s1", "trace-r"), TestContext.Current.CancellationToken);

    private void Pending() =>
        _order.AwaitConfirmation(ItemId, "unknown", new TransitionContext(_clock.GetUtcNow(), "test")).IsSuccess.ShouldBeTrue();

    private Task<bool> Reconcile() => Orchestrator(new NoTravellers()).ReconcileAsync(_order.Id, TestContext.Current.CancellationToken);

    private BookingOrchestrator Orchestrator(IOrderTravellers travellers) =>
        new(_store, _bookings, _hotelBookings, travellers, _clock, Options.Create(new BookingReconciliationOptions()), NullLogger<BookingOrchestrator>.Instance);

    private sealed class Travellers : IOrderTravellers
    {
        public Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<BookingTravellers?>(new("ada@example.com", "+447700900123",
                [new BookingTraveller(TravellerType.Adult, "Ada", "Lovelace", new DateOnly(1990, 12, 10), TravellerGenderType.Female, null)]));
    }

    // A child named before the adults, and a teenager the airline rule calls an adult: the lead guest (an adult) still comes
    // first, and the teenager is a child of 14 to the hotel.
    private sealed class Guests : IOrderTravellers
    {
        public Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<BookingTravellers?>(new("ada@example.com", "+447700900123",
            [
                new BookingTraveller(TravellerType.Child, "Ada", "Lovelace", new DateOnly(2019, 12, 10), TravellerGenderType.Female, null),
                new BookingTraveller(TravellerType.Adult, "Ada", "Lovelace", new DateOnly(1990, 12, 10), TravellerGenderType.Female, null),
                new BookingTraveller(TravellerType.Adult, "George", "Byron", new DateOnly(1988, 1, 22), TravellerGenderType.Male, null),
                new BookingTraveller(TravellerType.Adult, "Allegra", "Byron", new DateOnly(2012, 6, 1), TravellerGenderType.Female, null), // 14 at check-out
            ]));
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

        /// <summary>Published but not yet saved (the review check leaves the save to its handler).</summary>
        public IReadOnlyList<IIntegrationEvent> Pending => _pending;

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

        public Task<IReadOnlyList<Order>> SearchAsync(string? bookingReference, Guid? orderId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Order>> FindWithItemStatusAsync(OrderItemStatus status, (DateTimeOffset CreatedAt, Guid Id)? after, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Audit(TravelBooking.BuildingBlocks.Audit.AuditEntry entry)
        {
        }
    }
}
