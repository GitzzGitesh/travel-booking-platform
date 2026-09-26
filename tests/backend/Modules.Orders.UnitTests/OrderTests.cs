using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>The flight order item state machine (booking-lifecycle.md): every legal and illegal transition.</summary>
public sealed class OrderTests
{
    internal static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    internal static readonly Money Price = new(270m, new CurrencyCode("XTS"));
    private static readonly TransitionContext _system = new(Now.AddMinutes(1), "system", "trace-1");

    // Every status an item can actually reach (Draft is transient inside CreateForFlight).
    private static readonly FlightOrderItemStatus[] _reachable = Enum.GetValues<FlightOrderItemStatus>().Where(s => s is not FlightOrderItemStatus.Draft).ToArray();

    // Item-level transitions: target -> allowed sources (booking-lifecycle.md; Booking -> ManualReview for a mismatch).
    // Booking itself is order-level (StartBooking) and tested separately.
    private static readonly Dictionary<FlightOrderItemStatus, FlightOrderItemStatus[]> _legal = new()
    {
        [FlightOrderItemStatus.Abandoned] = [FlightOrderItemStatus.AwaitingPayment],
        [FlightOrderItemStatus.PendingConfirmation] = [FlightOrderItemStatus.Booking],
        [FlightOrderItemStatus.ManualReview] = [FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation],
        [FlightOrderItemStatus.Confirmed] = [FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation, FlightOrderItemStatus.ManualReview],
        [FlightOrderItemStatus.Failed] = [FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation, FlightOrderItemStatus.ManualReview],
    };

    [Fact]
    public void A_new_flight_order_is_awaiting_payment_at_the_agreed_price_with_its_history()
    {
        var order = NewOrder();

        var item = order.Items.ShouldHaveSingleItem();
        item.Status.ShouldBe(FlightOrderItemStatus.AwaitingPayment);
        item.AgreedPrice.ShouldBe(Price);
        item.AcceptedPriceQuoteId.ShouldBeNull();
        order.Total.ShouldBe(Price);
        order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        order.Timeline.Select(e => (e.FromStatus, e.ToStatus)).ShouldBe([(null, "Draft"), ("Draft", "AwaitingPayment")]);
        order.Timeline.ShouldAllBe(e => e.Actor == "customer" && e.CorrelationId == "trace-0" && e.OrderId == order.Id && e.ItemId == item.Id);
    }

    [Fact]
    public void An_accepted_price_change_is_kept_as_consent_evidence()
    {
        var quote = Guid.NewGuid();

        var order = Order.CreateForFlight("cust-1", "key-1", Guid.NewGuid(), Price, Now.AddMinutes(30), new PriceConsent(quote, Now.AddMinutes(-2)), new TransitionContext(Now, "customer"));

        order.Items[0].AcceptedPriceQuoteId.ShouldBe(quote);
        order.Items[0].PriceAcceptedAt.ShouldBe(Now.AddMinutes(-2));
        order.Timeline[0].Reason.ShouldContain(quote.ToString());
    }

    public static TheoryData<string, string> EveryItemTransition()
    {
        var data = new TheoryData<string, string>();
        foreach (var from in _reachable)
        {
            foreach (var to in _legal.Keys.Where(to => to != from))
            {
                data.Add(from.ToString(), to.ToString());
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryItemTransition))]
    public void Only_documented_transitions_are_legal_and_each_is_recorded(string fromName, string toName)
    {
        var from = Enum.Parse<FlightOrderItemStatus>(fromName);
        var to = Enum.Parse<FlightOrderItemStatus>(toName);
        var order = OrderAt(from, out var item);
        var entries = order.Timeline.Count;

        var result = Apply(order, item, to);

        if (_legal[to].Contains(from))
        {
            result.Value.ShouldBe(to);
            order.Items[0].Status.ShouldBe(to);
            var entry = order.Timeline.Last();
            (entry.FromStatus, entry.ToStatus, entry.Actor, entry.CorrelationId).ShouldBe((from.ToString(), to.ToString(), "system", "trace-1"));
            order.Timeline.Count.ShouldBe(entries + 1);
        }
        else
        {
            result.Error.ShouldBe(new OrderTransitionError.Illegal(from, to));
            order.Items[0].Status.ShouldBe(from);
            order.Timeline.Count.ShouldBe(entries);
        }
    }

    [Fact]
    public void Booking_starts_for_the_whole_order_only_with_a_payment_authorization_recorded_on_the_timeline()
    {
        var order = NewOrder();

        order.StartBooking(" ", _system).Error.ShouldBe(new OrderTransitionError.MissingReference("paymentAuthorizationId"));
        order.StartBooking("auth-1", _system).Value.ShouldBe(OrderStatus.Pending);

        order.PaymentAuthorizationId.ShouldBe("auth-1");
        order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Booking);
        order.Timeline.Last().ProviderReference.ShouldBe("auth-1");
    }

    [Fact]
    public void Replaying_the_same_authorization_is_a_no_op_and_another_one_is_a_conflict()
    {
        var order = OrderAt(FlightOrderItemStatus.PendingConfirmation, out _);
        var entries = order.Timeline.Count;

        order.StartBooking("auth-1", _system).IsSuccess.ShouldBeTrue();
        order.StartBooking("auth-2", _system).Error.ShouldBe(new OrderTransitionError.ConflictingReference("paymentAuthorizationId"));

        order.Timeline.Count.ShouldBe(entries);
        order.Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation);
    }

    [Fact]
    public void An_abandoned_order_cannot_start_booking()
    {
        var order = OrderAt(FlightOrderItemStatus.Abandoned, out _);

        order.StartBooking("auth-1", _system).Error.ShouldBe(new OrderTransitionError.Illegal(FlightOrderItemStatus.Abandoned, FlightOrderItemStatus.Booking));
    }

    [Fact]
    public void F02_an_item_whose_offer_expired_cannot_be_booked()
    {
        var order = NewOrder();

        var result = order.StartBooking("auth-1", new TransitionContext(Now.AddMinutes(30), "system"));

        result.Error.ShouldBe(new OrderTransitionError.OfferExpired(order.Items[0].Id));
        order.Items[0].Status.ShouldBe(FlightOrderItemStatus.AwaitingPayment);
        order.PaymentAuthorizationId.ShouldBeNull();
    }

    [Fact]
    public void Re_applying_a_transition_is_a_no_op_and_adds_nothing_to_the_timeline()
    {
        var order = OrderAt(FlightOrderItemStatus.PendingConfirmation, out var item);
        order.Confirm(item, "mock", "ABC234", _system);
        var entries = order.Timeline.Count;

        order.Confirm(item, "mock", "ABC234", _system).Value.ShouldBe(FlightOrderItemStatus.Confirmed);

        order.Timeline.Count.ShouldBe(entries);
    }

    [Fact]
    public void A_confirmation_records_the_supplier_reference_and_a_different_one_later_is_a_conflict()
    {
        var order = OrderAt(FlightOrderItemStatus.Booking, out var item);
        order.Confirm(item, "mock", "ABC234", _system);

        order.Confirm(item, "mock", "XYZ789", _system).Error.ShouldBe(new OrderTransitionError.ConflictingReference("supplierLocator"));
        order.Items[0].SupplierLocator.ShouldBe("ABC234");
        order.Timeline.Last().ProviderReference.ShouldBe("mock:ABC234");
    }

    [Fact]
    public void Confirmation_requires_the_supplier_locator()
    {
        var order = OrderAt(FlightOrderItemStatus.Booking, out var item);

        order.Confirm(item, "mock", "", _system).Error.ShouldBeOfType<OrderTransitionError.MissingReference>();
        order.Items[0].Status.ShouldBe(FlightOrderItemStatus.Booking);
    }

    [Fact]
    public void An_unknown_item_is_reported()
    {
        var order = NewOrder();

        order.Fail(Guid.NewGuid(), "x", _system).Error.ShouldBeOfType<OrderTransitionError.ItemNotFound>();
    }

    [Fact]
    public void The_order_status_is_derived_from_its_items()
    {
        Order.Derive([FlightOrderItemStatus.Draft]).ShouldBe(OrderStatus.Draft);
        Order.Derive([FlightOrderItemStatus.AwaitingPayment]).ShouldBe(OrderStatus.AwaitingPayment);
        Order.Derive([FlightOrderItemStatus.Confirmed, FlightOrderItemStatus.PendingConfirmation]).ShouldBe(OrderStatus.Pending);
        Order.Derive([FlightOrderItemStatus.Booking]).ShouldBe(OrderStatus.Pending);
        Order.Derive([FlightOrderItemStatus.ManualReview, FlightOrderItemStatus.Failed]).ShouldBe(OrderStatus.Pending);
        Order.Derive([FlightOrderItemStatus.Confirmed, FlightOrderItemStatus.Confirmed]).ShouldBe(OrderStatus.Confirmed);
        Order.Derive([FlightOrderItemStatus.Confirmed, FlightOrderItemStatus.Failed]).ShouldBe(OrderStatus.PartiallyConfirmed);
        Order.Derive([FlightOrderItemStatus.Failed, FlightOrderItemStatus.Failed]).ShouldBe(OrderStatus.Failed);
        Order.Derive([FlightOrderItemStatus.Abandoned]).ShouldBe(OrderStatus.Abandoned);
    }

    [Fact]
    public void Refreshing_the_offer_takes_the_new_expiry_without_a_timeline_entry()
    {
        var order = NewOrder();
        var (item, revision, entries) = (order.Items[0], order.Revision, order.Timeline.Count);

        order.RefreshOffer(item.Id, Price, Now.AddMinutes(50), null, _system).IsSuccess.ShouldBeTrue();

        (item.OfferExpiresAt, item.AgreedPrice).ShouldBe((Now.AddMinutes(50), Price));
        order.Revision.ShouldBeGreaterThan(revision);
        order.Timeline.Count.ShouldBe(entries);
    }

    [Fact]
    public void F01_an_accepted_price_change_is_adopted_with_its_consent_and_recorded()
    {
        var order = NewOrder();
        var item = order.Items[0];
        var consent = new PriceConsent(Guid.NewGuid(), Now);

        order.RefreshOffer(item.Id, Price with { Amount = 310.5m }, Now.AddMinutes(40), consent, _system).IsSuccess.ShouldBeTrue();

        (item.AgreedPrice.Amount, item.AcceptedPriceQuoteId, item.PriceAcceptedAt).ShouldBe((310.5m, consent.AcceptedPriceQuoteId, consent.AcceptedAt));
        order.Total.Amount.ShouldBe(310.5m);
        var entry = order.Timeline[^1];
        (entry.FromStatus, entry.ToStatus).ShouldBe(("AwaitingPayment", "AwaitingPayment"));
        entry.Reason.ShouldContain("310.5");
    }

    [Fact]
    public void F01_a_different_price_without_new_consent_is_refused()
    {
        var order = NewOrder();
        var item = order.Items[0];
        var consent = new PriceConsent(Guid.NewGuid(), Now);
        order.RefreshOffer(item.Id, Price with { Amount = 300m }, Now.AddMinutes(40), consent, _system);

        order.RefreshOffer(item.Id, Price with { Amount = 350m }, Now.AddMinutes(40), null, _system).Error.ShouldBeOfType<OrderTransitionError.PriceNotAccepted>();
        order.RefreshOffer(item.Id, Price with { Amount = 350m }, Now.AddMinutes(40), consent, _system).Error.ShouldBeOfType<OrderTransitionError.PriceNotAccepted>();
        order.RefreshOffer(item.Id, new Money(300m, new CurrencyCode("EUR")), Now.AddMinutes(40), new PriceConsent(Guid.NewGuid(), Now), _system)
            .Error.ShouldBeOfType<OrderTransitionError.PriceNotAccepted>();
        item.AgreedPrice.Amount.ShouldBe(300m);
    }

    [Theory]
    [InlineData("Booking")]
    [InlineData("Abandoned")]
    [InlineData("Confirmed")]
    public void Only_an_item_awaiting_payment_takes_new_terms(string status)
    {
        var order = OrderAt(Enum.Parse<FlightOrderItemStatus>(status), out var item);

        order.RefreshOffer(item, Price, Now.AddMinutes(50), null, _system).Error.ShouldBeOfType<OrderTransitionError.Illegal>();
        order.RefreshOffer(Guid.NewGuid(), Price, Now.AddMinutes(50), null, _system).Error.ShouldBeOfType<OrderTransitionError.ItemNotFound>();
    }

    internal static Order NewOrder(Guid? selectedOfferId = null) =>
        Order.CreateForFlight("cust-1", "key-1", selectedOfferId ?? Guid.NewGuid(), Price, Now.AddMinutes(30), null, new TransitionContext(Now, "customer", "trace-0"));

    private static Result<FlightOrderItemStatus, OrderTransitionError> Apply(Order order, Guid item, FlightOrderItemStatus to) => to switch
    {
        FlightOrderItemStatus.Abandoned => order.Abandon(item, "Offer expired before payment", _system),
        FlightOrderItemStatus.PendingConfirmation => order.AwaitConfirmation(item, "Supplier timeout", _system),
        FlightOrderItemStatus.ManualReview => order.RequireManualReview(item, "Mismatch", _system),
        FlightOrderItemStatus.Confirmed => order.Confirm(item, "mock", "ABC234", _system),
        FlightOrderItemStatus.Failed => order.Fail(item, "Rejected", _system),
        _ => throw new ArgumentOutOfRangeException(nameof(to)),
    };

    /// <summary>An order whose item has been driven to <paramref name="status"/> by legal transitions.</summary>
    private static Order OrderAt(FlightOrderItemStatus status, out Guid item)
    {
        var order = NewOrder();
        item = order.Items[0].Id;
        if (status is FlightOrderItemStatus.AwaitingPayment)
        {
            return order;
        }

        if (status is FlightOrderItemStatus.Abandoned)
        {
            Apply(order, item, FlightOrderItemStatus.Abandoned).IsSuccess.ShouldBeTrue();
            return order;
        }

        order.StartBooking("auth-1", _system).IsSuccess.ShouldBeTrue();
        if (status is not FlightOrderItemStatus.Booking)
        {
            Apply(order, item, status).IsSuccess.ShouldBeTrue();
        }

        return order;
    }
}
