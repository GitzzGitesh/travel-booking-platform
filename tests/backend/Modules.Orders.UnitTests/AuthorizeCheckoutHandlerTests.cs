using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// The checkout's payment step (ADR 0005: revalidate → authorize → book): the customer's own order only, the supplier
/// asked first, the server-side total authorized, and Booking only on an authorized payment (F-01..F-03, F-20, F-21, F-32).
/// </summary>
public sealed class AuthorizeCheckoutHandlerTests
{
    private readonly FakeTimeProvider _clock = new(OrderTests.Now.AddMinutes(1));
    private readonly FakeStore _store = new();
    private readonly StubSelections _selections = new();
    private readonly StubPayments _payments = new();
    private readonly Order _order = OrderTests.NewOrder();

    public AuthorizeCheckoutHandlerTests() => _store.Orders.Add(_order);

    [Fact]
    public async Task An_authorized_payment_starts_the_booking_with_the_payment_on_the_timeline()
    {
        var result = (await Handle()).Value;

        result.ShouldBe(new CheckoutResult(_order.Id, CheckoutStatus.BookingStarted, _payments.PaymentId));
        _order.Status.ShouldBe(OrderStatus.Pending);
        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Booking);
        _order.PaymentAuthorizationId.ShouldBe(_payments.PaymentId.ToString());
        _order.Timeline[^1].ProviderReference.ShouldBe(_payments.PaymentId.ToString());
        _selections.Revalidated.ShouldBe([_order.Items[0].SelectedOfferId]);
        var request = _payments.Requests.ShouldHaveSingleItem();
        request.ShouldBe(new OrderPaymentRequest(_order.Id, "cust-1", "pay-1", OrderTests.Price, "pm_test", "trace-1"));
    }

    [Fact]
    public async Task The_fresh_offer_expiry_is_adopted_before_payment()
    {
        _selections.Next = Bookable(expiresAt: OrderTests.Now.AddMinutes(55));

        await Handle();

        _order.Items[0].OfferExpiresAt.ShouldBe(OrderTests.Now.AddMinutes(55));
    }

    [Fact]
    public async Task F01_an_accepted_price_change_is_what_gets_authorized()
    {
        var quote = Guid.NewGuid();
        _selections.Next = Bookable(price: OrderTests.Price with { Amount = 310.5m }, quote: quote);

        await Handle();

        _payments.Requests.ShouldHaveSingleItem().Amount.Amount.ShouldBe(310.5m);
        _order.Items[0].AcceptedPriceQuoteId.ShouldBe(quote);
    }

    [Theory]
    [InlineData(FlightSelectionUnavailable.NeedsPriceCheck, typeof(CheckoutFailure.PriceChanged))] // F-01
    [InlineData(FlightSelectionUnavailable.Expired, typeof(CheckoutFailure.OfferExpired))] // F-02
    [InlineData(FlightSelectionUnavailable.NotFound, typeof(CheckoutFailure.OfferExpired))]
    [InlineData(FlightSelectionUnavailable.SoldOut, typeof(CheckoutFailure.SoldOut))] // F-03
    [InlineData(FlightSelectionUnavailable.TryAgain, typeof(CheckoutFailure.TryAgain))]
    internal async Task An_offer_that_cannot_be_booked_is_never_paid_for(FlightSelectionUnavailable reason, Type expected)
    {
        _selections.Next = Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(reason);

        (await Handle()).Error.ShouldBeOfType(expected);

        _payments.Requests.ShouldBeEmpty();
        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task F01_a_different_price_without_consent_is_never_paid_for()
    {
        _selections.Next = Bookable(price: OrderTests.Price with { Amount = 199m });

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.PriceChanged>();

        _payments.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task F02_an_offer_about_to_expire_is_not_paid_for()
    {
        _selections.Next = Bookable(expiresAt: _clock.GetUtcNow() + AuthorizeCheckoutHandler.MinimumOfferValidity - TimeSpan.FromSeconds(1));

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.OfferExpired>();

        _payments.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(OrderPaymentStatus.ActionRequired, CheckoutStatus.ActionRequired)] // F-21
    [InlineData(OrderPaymentStatus.Declined, CheckoutStatus.Declined)] // F-20
    [InlineData(OrderPaymentStatus.Pending, CheckoutStatus.PaymentPending)]
    [InlineData(OrderPaymentStatus.Failed, CheckoutStatus.PaymentFailed)]
    [InlineData(OrderPaymentStatus.ManualReview, CheckoutStatus.PaymentManualReview)]
    internal async Task Without_an_authorization_the_order_keeps_awaiting_payment(OrderPaymentStatus payment, CheckoutStatus expected)
    {
        _payments.Status = payment;

        var result = (await Handle()).Value;

        result.Status.ShouldBe(expected);
        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        _order.PaymentAuthorizationId.ShouldBeNull();
    }

    [Theory]
    [InlineData(OrderPaymentFailure.IdempotencyKeyReused, typeof(CheckoutFailure.IdempotencyKeyReused))]
    [InlineData(OrderPaymentFailure.InvalidPaymentMethod, typeof(CheckoutFailure.InvalidPaymentMethod))]
    [InlineData(OrderPaymentFailure.PaymentInProgress, typeof(CheckoutFailure.PaymentInProgress))] // F-32
    [InlineData(OrderPaymentFailure.InvalidRequest, typeof(CheckoutFailure.InvalidRequest))]
    internal async Task Payment_refusals_are_reported_and_change_nothing(OrderPaymentFailure failure, Type expected)
    {
        _payments.Failure = failure;

        (await Handle()).Error.ShouldBeOfType(expected);

        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task A_replay_after_the_booking_started_returns_it_without_another_payment_or_supplier_call()
    {
        await Handle();

        var replay = (await Handle(key: "pay-2")).Value;

        replay.ShouldBe(new CheckoutResult(_order.Id, CheckoutStatus.BookingStarted, _payments.PaymentId));
        (_payments.Requests.Count, _selections.Revalidated.Count).ShouldBe((1, 1));
    }

    [Theory]
    [InlineData(FlightSelectionUnavailable.Expired)]
    [InlineData(FlightSelectionUnavailable.TryAgain)]
    internal async Task An_attempt_made_with_this_key_is_finished_before_the_supplier_is_asked_again(FlightSelectionUnavailable supplierNow)
    {
        _payments.Status = OrderPaymentStatus.Pending;
        await Handle(); // the authorization timed out
        _payments.Status = OrderPaymentStatus.Authorized;
        _payments.Resumable = true;
        _selections.Next = Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(supplierNow);

        var replay = (await Handle()).Value;

        replay.Status.ShouldBe(CheckoutStatus.BookingStarted);
        (_selections.Revalidated.Count, _payments.Requests.Count, _payments.Resumed).ShouldBe((1, 1, 1));
    }

    [Fact]
    public async Task A_hold_for_another_amount_is_never_booked_on_and_is_noted_for_release()
    {
        _payments.Amount = OrderTests.Price with { Amount = 199m };
        _payments.Resumable = true;

        var result = await Handle();

        result.Error.ShouldBe(new CheckoutFailure.AuthorizedButNotBookable(_payments.PaymentId));
        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        var note = _order.Timeline[^1];
        (note.FromStatus, note.ToStatus, note.ProviderReference).ShouldBe(("AwaitingPayment", "AwaitingPayment", _payments.PaymentId.ToString()));
        note.Reason.ShouldContain("released");
    }

    [Fact]
    public async Task A_hold_whose_offer_expired_meanwhile_is_noted_once_for_release()
    {
        _payments.Resumable = true;
        _clock.Advance(TimeSpan.FromHours(1)); // past the offer's expiry

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.AuthorizedButNotBookable>();
        var entries = _order.Timeline.Count;
        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.AuthorizedButNotBookable>();

        _order.Timeline.Count.ShouldBe(entries);
        _order.PaymentAuthorizationId.ShouldBeNull();
        var release = _store.Published.ShouldHaveSingleItem().ShouldBeOfType<OrderPaymentReleaseRequested>(); // F-22: one release request
        (release.OrderId, release.PaymentId).ShouldBe((_order.Id, _payments.PaymentId));
    }

    [Fact]
    public void Tokens_and_customer_actions_are_never_printed()
    {
        new AuthorizeCheckout(_order.Id, "cust-1", "pay-1", "4242424242424242", null).ToString().ShouldNotContain("4242");
        new CheckoutResult(_order.Id, CheckoutStatus.ActionRequired, Guid.NewGuid(), CustomerAction: "secret_live_1").ToString().ShouldNotContain("secret_live_1");
    }

    [Fact]
    public async Task Another_customers_order_is_not_found()
    {
        (await Handle(customer: "cust-2")).Error.ShouldBeOfType<CheckoutFailure.NotFound>();

        (_payments.Requests.Count, _selections.Revalidated.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task An_order_past_payment_is_not_paid_again()
    {
        _order.Abandon(_order.Items[0].Id, "Offer expired", new TransitionContext(OrderTests.Now, "system"));

        (await Handle()).Error.ShouldBe(new CheckoutFailure.NotAwaitingPayment(OrderStatus.Abandoned));
    }

    [Theory]
    [InlineData("", "pay-1", "pm_test")]
    [InlineData("cust-1", " ", "pm_test")]
    [InlineData("cust-1", "pay-1", "")]
    public async Task Invalid_requests_are_refused(string customer, string key, string token)
    {
        var result = await new AuthorizeCheckoutHandler(_store, _selections, _payments, _clock)
            .HandleAsync(new AuthorizeCheckout(_order.Id, customer, key, token, "trace-1"), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<CheckoutFailure.InvalidRequest>();
    }

    [Fact]
    public async Task A_concurrent_order_change_before_payment_charges_nothing()
    {
        _store.SaveSucceeds = false;

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.TryAgain>();

        _payments.Requests.ShouldBeEmpty();
    }

    private Task<Result<CheckoutResult, CheckoutFailure>> Handle(string customer = "cust-1", string key = "pay-1") =>
        new AuthorizeCheckoutHandler(_store, _selections, _payments, _clock)
            .HandleAsync(new AuthorizeCheckout(_order.Id, customer, key, "pm_test", "trace-1"), TestContext.Current.CancellationToken);

    private Result<BookableFlightSelection, FlightSelectionUnavailable> Bookable(Money? price = null, DateTimeOffset? expiresAt = null, Guid? quote = null) =>
        Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(new BookableFlightSelection(
            _order.Items[0].SelectedOfferId, price ?? OrderTests.Price, expiresAt ?? OrderTests.Now.AddMinutes(30), quote, quote is null ? null : OrderTests.Now));

    private sealed class StubSelections : IFlightSelections
    {
        public Result<BookableFlightSelection, FlightSelectionUnavailable>? Next { get; set; }

        public List<Guid> Revalidated { get; } = [];

        public Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The checkout always asks the supplier.");

        public Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, CancellationToken cancellationToken)
        {
            Revalidated.Add(selectedOfferId);
            return Task.FromResult(Next ?? Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(
                new BookableFlightSelection(selectedOfferId, OrderTests.Price, OrderTests.Now.AddMinutes(30), null, null)));
        }
    }

    private sealed class StubPayments : IOrderPayments
    {
        public Guid PaymentId { get; } = Guid.NewGuid();

        public Money Amount { get; set; } = OrderTests.Price;

        /// <summary>Whether an attempt already exists for the key (a repeated request).</summary>
        public bool Resumable { get; set; }

        public int Resumed { get; private set; }

        public OrderPaymentStatus Status { get; set; } = OrderPaymentStatus.Authorized;

        public OrderPaymentFailure? Failure { get; set; }

        public List<OrderPaymentRequest> Requests { get; } = [];

        public Task<Result<OrderPaymentResult, OrderPaymentFailure>> AuthorizeAsync(OrderPaymentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Failure is { } failure
                ? Result<OrderPaymentResult, OrderPaymentFailure>.Failure(failure)
                : Result<OrderPaymentResult, OrderPaymentFailure>.Success(new OrderPaymentResult(PaymentId, Status, Amount)));
        }

        public Task<LiveOrderPayment?> FindLiveAsync(Guid orderId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderPaymentResult?> ResumeAsync(Guid orderId, string customerId, string idempotencyKey, string? correlationId, CancellationToken cancellationToken)
        {
            if (!Resumable)
            {
                return Task.FromResult<OrderPaymentResult?>(null);
            }

            Resumed++;
            return Task.FromResult<OrderPaymentResult?>(new OrderPaymentResult(PaymentId, Status, Amount));
        }
    }

    private sealed class FakeStore : IOrderStore
    {
        public List<Order> Orders { get; } = [];

        public bool SaveSucceeds { get; set; } = true;

        public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId && o.CustomerId == customerId));

        public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(SaveSucceeds);

        public List<IIntegrationEvent> Published { get; } = [];

        public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
            where TEvent : IIntegrationEvent => Published.Add(integrationEvent);

        public Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
