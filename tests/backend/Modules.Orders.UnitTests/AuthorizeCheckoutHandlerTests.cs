using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// The checkout (ADR 0005: revalidate → authorize → book → capture): the customer's own order only, the supplier asked
/// first, the server-side total authorized, booked only on an authorized payment, and charged only for a confirmed
/// booking (F-01..F-03, F-10, F-20, F-21, F-32).
/// </summary>
public sealed class AuthorizeCheckoutHandlerTests
{
    private readonly FakeTimeProvider _clock = new(OrderTests.Now.AddMinutes(1));
    private readonly FakeStore _store = new();
    private readonly StubSelections _selections = new();
    private readonly StubPayments _payments = new();
    private readonly StubTravellers _travellers = new();
    private readonly StubBookings _bookings = new();
    private readonly Order _order = OrderTests.NewOrder();

    public AuthorizeCheckoutHandlerTests() => _store.Orders.Add(_order);

    [Fact]
    public async Task An_authorized_payment_books_the_flight_and_only_then_requests_the_charge()
    {
        var result = (await Handle()).Value;

        result.ShouldBe(new CheckoutResult(_order.Id, CheckoutStatus.Booked, _payments.PaymentId));
        _order.Status.ShouldBe(OrderStatus.Confirmed);
        (_order.Items[0].Status, _order.Items[0].SupplierLocator, _order.Items[0].Ticketing).ShouldBe((FlightOrderItemStatus.Confirmed, "LOC123", TicketingStatus.Issued));
        var booked = _bookings.Booked.ShouldHaveSingleItem();
        (booked.ClientReference, booked.AgreedPrice, booked.Passengers.Count).ShouldBe((_order.Items[0].Id.ToString(), OrderTests.Price, 1));
        var capture = _store.Published.OfType<OrderPaymentCaptureRequested>().ShouldHaveSingleItem();
        (capture.PaymentId, capture.Amount).ShouldBe((_payments.PaymentId, OrderTests.Price));
        // The customer's notice, in the same save as the charge (ADR 0024): what was booked and charged, no personal data.
        var settled = _store.Published.OfType<OrderBookingSettled>().ShouldHaveSingleItem();
        (settled.OrderId, settled.Outcome, settled.Charged).ShouldBe((_order.Id, BookingOutcome.Confirmed, OrderTests.Price));
        settled.BookingReferences.ShouldBe(["LOC123"]);
        _order.PaymentAuthorizationId.ShouldBe(_payments.PaymentId.ToString());
        _order.Timeline[^1].ProviderReference.ShouldBe(_payments.PaymentId.ToString());
        _selections.Revalidated.ShouldBe([_order.Items[0].SelectedOfferId]);
        _selections.RevalidatedFor.ShouldBe([_order.CustomerId]); // only the owner's selection may be paid for
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
    [InlineData(FlightSelectionUnavailable.SupplierCannotBook, typeof(CheckoutFailure.SupplierCannotBook))] // Q6: a search-only adapter
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

        replay.ShouldBe(new CheckoutResult(_order.Id, CheckoutStatus.Booked, _payments.PaymentId));
        _bookings.Booked.Count.ShouldBe(1); // never a second booking
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

        replay.Status.ShouldBe(CheckoutStatus.Booked);
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
        var release = _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem(); // F-22: one release request
        (release.OrderId, release.PaymentId).ShouldBe((_order.Id, _payments.PaymentId));
    }

    [Theory]
    [InlineData(CheckoutStatus.Booked, CustomerPaymentState.Accepted)]
    [InlineData(CheckoutStatus.BookingPending, CustomerPaymentState.Accepted)]
    [InlineData(CheckoutStatus.BookingFailed, CustomerPaymentState.Released)]
    [InlineData(CheckoutStatus.ActionRequired, CustomerPaymentState.ActionRequired)]
    [InlineData(CheckoutStatus.Declined, CustomerPaymentState.Declined)]
    [InlineData(CheckoutStatus.PaymentFailed, CustomerPaymentState.Declined)]
    [InlineData(CheckoutStatus.PaymentPending, CustomerPaymentState.Pending)]
    [InlineData(CheckoutStatus.PaymentManualReview, CustomerPaymentState.Unavailable)] // never named as a review
    internal void Customers_see_only_generic_payment_states(CheckoutStatus status, CustomerPaymentState expected)
    {
        new CheckoutResult(_order.Id, status, Guid.NewGuid()).CustomerState.ShouldBe(expected);
        Enum.GetValues<CheckoutStatus>().ShouldAllBe(s => Enum.IsDefined(new CheckoutResult(_order.Id, s, null).CustomerState)); // every status is mapped
    }

    [Fact]
    public async Task A_decline_reaches_checkout_without_a_reason()
    {
        _payments.Status = OrderPaymentStatus.Declined;

        var result = (await Handle()).Value;

        (result.Status, result.CustomerState).ShouldBe((CheckoutStatus.Declined, CustomerPaymentState.Declined));
        typeof(CheckoutResult).GetProperties().Select(p => p.Name).ShouldNotContain("DeclineReason");
    }

    [Fact]
    public async Task The_payment_attempt_limit_refuses_checkout_before_anything_is_booked()
    {
        _payments.Failure = OrderPaymentFailure.AttemptLimitReached;

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.AttemptLimitReached>();
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
        var result = await new AuthorizeCheckoutHandler(_store, _selections, _payments, _travellers, Orchestrator(), _clock)
            .HandleAsync(new AuthorizeCheckout(_order.Id, customer, key, token, "trace-1"), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<CheckoutFailure.InvalidRequest>();
    }

    [Fact]
    public async Task An_order_that_cannot_be_read_consistently_is_answered_try_again_and_nothing_is_paid()
    {
        _store.FindOwnedKeepsChanging = true;

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.TryAgain>();

        _payments.Requests.ShouldBeEmpty();
        _bookings.Booked.ShouldBeEmpty();
    }

    // Booking started (saved) and the supplier booked, but the outcome's save lost a race and the order could not be read
    // back: the customer is told the truth (pending), never a server error, and nothing is booked again.
    [Fact]
    public async Task A_booking_whose_outcome_cannot_be_read_back_is_pending_not_an_error()
    {
        _store.Saves.Enqueue(true); // the refreshed terms
        _store.Saves.Enqueue(true); // StartBooking
        _store.SaveSucceeds = false; // the outcome
        _store.FindKeepsChanging = true;

        var result = (await Handle()).Value;

        result.ShouldBe(new CheckoutResult(_order.Id, CheckoutStatus.BookingPending, _payments.PaymentId));
        _bookings.Booked.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_concurrent_order_change_before_payment_charges_nothing()
    {
        _store.SaveSucceeds = false;

        (await Handle()).Error.ShouldBeOfType<CheckoutFailure.TryAgain>();

        _payments.Requests.ShouldBeEmpty();
    }

    private Task<Result<CheckoutResult, CheckoutFailure>> Handle(string customer = "cust-1", string key = "pay-1") =>
        new AuthorizeCheckoutHandler(_store, _selections, _payments, _travellers, Orchestrator(), _clock)
            .HandleAsync(new AuthorizeCheckout(_order.Id, customer, key, "pm_test", "trace-1"), TestContext.Current.CancellationToken);

    private Result<BookableFlightSelection, FlightSelectionUnavailable> Bookable(Money? price = null, DateTimeOffset? expiresAt = null, Guid? quote = null) =>
        Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(new BookableFlightSelection(
            _order.Items[0].SelectedOfferId, price ?? OrderTests.Price, expiresAt ?? OrderTests.Now.AddMinutes(30), quote, quote is null ? null : OrderTests.Now));

    [Fact]
    public async Task No_payment_until_the_travellers_and_contact_are_complete()
    {
        _travellers.Readiness = new OrderTravellersReadiness(1, 0, 0, ContactProvided: false, 0);

        (await Handle()).Error.ShouldBe(new CheckoutFailure.TravellersIncomplete(DocumentsRequired: false));

        _payments.Requests.ShouldBeEmpty();
        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task Documents_the_supplier_now_requires_are_recorded_and_needed_before_payment()
    {
        _selections.Next = Bookable() is { IsSuccess: true } bookable
            ? Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(bookable.Value with { DocumentsRequired = true })
            : throw new InvalidOperationException();

        var result = await Handle();

        result.Error.ShouldBe(new CheckoutFailure.TravellersIncomplete(DocumentsRequired: true));
        _order.Items[0].TravellerNeeds!.DocumentsRequired.ShouldBeTrue(); // saved, so the customer is asked for them
        _order.Timeline.ShouldContain(e => e.Reason == "The supplier now requires travel documents");
        _payments.Requests.ShouldBeEmpty();

        _travellers.Readiness = _travellers.Readiness with { DocumentsProvided = 1 };
        (await Handle(key: "pay-2")).Value.Status.ShouldBe(CheckoutStatus.Booked);
    }

    [Fact]
    public async Task An_order_without_recorded_traveller_needs_is_never_paid()
    {
        _store.Orders.Clear();
        var legacy = Order.CreateForFlight("cust-1", "key-1", Guid.NewGuid(), OrderTests.Price, OrderTests.Now.AddMinutes(30), null, new TransitionContext(OrderTests.Now, "customer"));
        _store.Orders.Add(legacy);

        var result = await new AuthorizeCheckoutHandler(_store, _selections, _payments, _travellers, Orchestrator(), _clock)
            .HandleAsync(new AuthorizeCheckout(legacy.Id, "cust-1", "pay-1", "pm_test", "trace-1"), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<CheckoutFailure.TravellersIncomplete>();
        _payments.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Travellers_changed_while_the_payment_was_authorized_stop_the_booking_and_release_the_hold()
    {
        _travellers.Later = new OrderTravellersReadiness(1, 0, 0, ContactProvided: false, 0); // edited in between

        var result = await Handle();

        result.Error.ShouldBe(new CheckoutFailure.AuthorizedButNotBookable(_payments.PaymentId));
        _order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        _order.PaymentAuthorizationId.ShouldBeNull();
        _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_resumed_payment_is_booked_on_only_with_complete_travellers()
    {
        _payments.Status = OrderPaymentStatus.ActionRequired;
        await Handle(); // a challenge is open
        _payments.Status = OrderPaymentStatus.Authorized;
        _payments.Resumable = true;
        _travellers.Readiness = new OrderTravellersReadiness(0, 0, 0, ContactProvided: false, 0);

        var resumed = await Handle(); // the same key, after the challenge: no second revalidation or check before it

        resumed.Error.ShouldBe(new CheckoutFailure.AuthorizedButNotBookable(_payments.PaymentId));
        _order.PaymentAuthorizationId.ShouldBeNull();
    }

    [Fact]
    public async Task A_resumed_payment_on_an_order_without_recorded_traveller_needs_is_never_booked_on()
    {
        _store.Orders.Clear();
        var legacy = Order.CreateForFlight("cust-1", "key-1", Guid.NewGuid(), OrderTests.Price, OrderTests.Now.AddMinutes(30), null, new TransitionContext(OrderTests.Now, "customer"));
        _store.Orders.Add(legacy);
        _payments.Resumable = true;

        var result = await new AuthorizeCheckoutHandler(_store, _selections, _payments, _travellers, Orchestrator(), _clock)
            .HandleAsync(new AuthorizeCheckout(legacy.Id, "cust-1", "pay-1", "pm_test", "trace-1"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(new CheckoutFailure.AuthorizedButNotBookable(_payments.PaymentId));
        legacy.PaymentAuthorizationId.ShouldBeNull();
    }

    [Fact]
    public async Task A_supplier_refusal_fails_the_booking_releases_the_hold_and_charges_nothing()
    {
        _bookings.Next = new FlightBookingResult(FlightBookingStatus.NotBooked, "mock", Detail: "The flight sold out; nothing was booked");

        var result = (await Handle()).Value;

        result.Status.ShouldBe(CheckoutStatus.BookingFailed);
        result.CustomerState.ShouldBe(CustomerPaymentState.Released);
        _order.Status.ShouldBe(OrderStatus.Failed);
        _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem().PaymentId.ShouldBe(_payments.PaymentId);
        var settled = _store.Published.OfType<OrderBookingSettled>().ShouldHaveSingleItem();
        (settled.Outcome, settled.Charged, settled.BookingReferences.Count).ShouldBe((BookingOutcome.NotBooked, null, 0));
    }

    [Fact]
    public async Task An_unknown_booking_outcome_is_pending_neither_charged_nor_released_nor_sent_again()
    {
        _bookings.Next = new FlightBookingResult(FlightBookingStatus.Unknown, "mock");

        var first = (await Handle()).Value;
        var replay = (await Handle()).Value;

        (first.Status, replay.Status).ShouldBe((CheckoutStatus.BookingPending, CheckoutStatus.BookingPending));
        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation);
        _bookings.Booked.Count.ShouldBe(1);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_booking_not_as_agreed_goes_to_a_person_and_is_never_charged()
    {
        _bookings.Next = new FlightBookingResult(FlightBookingStatus.Mismatch, "mock", "LOC999");

        (await Handle()).Value.Status.ShouldBe(CheckoutStatus.BookingPending);

        _order.Items[0].Status.ShouldBe(FlightOrderItemStatus.ManualReview);
        _store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Travellers_that_cannot_be_read_at_booking_are_never_sent_and_the_hold_is_released()
    {
        _travellers.ForBooking = null;

        (await Handle()).Value.Status.ShouldBe(CheckoutStatus.BookingFailed);

        _bookings.Booked.ShouldBeEmpty();
        _store.Published.OfType<OrderPaymentReleaseRequested>().ShouldHaveSingleItem();
    }

    private FlightBookingOrchestrator Orchestrator() =>
        new(_store, _bookings, _travellers, _clock, Options.Create(new BookingReconciliationOptions()), NullLogger<FlightBookingOrchestrator>.Instance);

    private sealed class StubTravellers : IOrderTravellers
    {
        public BookingTravellers? ForBooking { get; set; } = new("ada@example.com", "+447700900123",
            [new BookingTraveller(TravellerType.Adult, "Ada", "Lovelace", new DateOnly(1990, 12, 10), TravellerGenderType.Female, null)]);

        public Task<BookingTravellers?> GetForBookingAsync(Guid orderId, string customerId, string? correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(ForBooking);

        /// <summary>Complete for one adult by default.</summary>
        public OrderTravellersReadiness Readiness { get; set; } = new(1, 0, 0, ContactProvided: true, 0);

        /// <summary>What later checks see, when the travellers change after the first check.</summary>
        public OrderTravellersReadiness? Later { get; set; }

        private int _checks;

        public Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
            Task.FromResult(_checks++ > 0 && Later is { } later ? later : Readiness);
    }

    private sealed class StubSelections : IFlightSelections
    {
        public Result<BookableFlightSelection, FlightSelectionUnavailable>? Next { get; set; }

        public List<Guid> Revalidated { get; } = [];

        /// <summary>The customer each revalidation was asked for: the order's owner, who must own the selection.</summary>
        public List<string> RevalidatedFor { get; } = [];

        public Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The checkout always asks the supplier.");

        public Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken)
        {
            Revalidated.Add(selectedOfferId);
            RevalidatedFor.Add(customerId);
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

        /// <summary>Answers for the next saves, in order; then <see cref="SaveSucceeds"/>.</summary>
        public Queue<bool> Saves { get; } = new();

        /// <summary>The store cannot read the order consistently (it keeps changing): FindAsync / FindOwnedAsync throw.</summary>
        public bool FindKeepsChanging { get; set; }

        public bool FindOwnedKeepsChanging { get; set; }

        public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) => FindKeepsChanging
            ? throw new OrderKeptChangingException(orderId)
            : Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) => FindOwnedKeepsChanging
            ? throw new OrderKeptChangingException(orderId)
            : Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId && o.CustomerId == customerId));

        public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(Saves.TryDequeue(out var answer) ? answer : SaveSucceeds);

        public List<IIntegrationEvent> Published { get; } = [];

        public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
            where TEvent : IIntegrationEvent => Published.Add(integrationEvent);

        public Task<IReadOnlyList<Guid>> FindWithExpiredUnpaidItemsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsReleaseRequestPendingAsync(Guid paymentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindBookingsToReconcileAsync(DateTimeOffset startedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Order>> FindWithItemStatusAsync(FlightOrderItemStatus status, (DateTimeOffset CreatedAt, Guid Id)? after, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Audit(TravelBooking.BuildingBlocks.Audit.AuditEntry entry)
        {
        }
    }
}
