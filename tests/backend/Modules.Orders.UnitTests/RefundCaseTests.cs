using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// Cancellations and refund cases (ADR 0027): a confirmed booking cancelled at the supplier becomes Cancelled (only a
/// confirmed one); a refund case is decided by a different person (staff id or account), within its window, once; a case
/// with nothing to refund is closed at once; the policy says when a second person is needed.
/// </summary>
public sealed class RefundCaseTests
{
    private static readonly DateTimeOffset _now = OrderTests.Now;
    private readonly TransitionContext _staff = new(_now, "staff:1", "trace-1");

    [Fact]
    public void Only_a_confirmed_booking_is_cancelled_and_the_order_follows()
    {
        var order = Confirmed();

        order.CancelConfirmed(order.Items[0].Id, " ", _staff).Error.ShouldBe(new OrderTransitionError.MissingReference("deskReference"));
        order.CancelConfirmed(order.Items[0].Id, "DESK-CXL-1", _staff).Value.ShouldBe(OrderItemStatus.Cancelled);
        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.Timeline[^1].ProviderReference.ShouldBe("DESK-CXL-1");
        order.CancelConfirmed(order.Items[0].Id, "DESK-CXL-1", _staff).IsSuccess.ShouldBeTrue(); // the same again: no change
        order.Timeline.Count(e => e.ToStatus == nameof(OrderItemStatus.Cancelled)).ShouldBe(1);

        var unbooked = OrderTests.NewOrder();
        unbooked.CancelConfirmed(unbooked.Items[0].Id, "DESK-CXL-2", _staff).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("staff-1", "account-b")] // the same person under another account
    [InlineData("staff-2", "ACCOUNT-A")] // another staff id for the same account
    public void Nobody_approves_their_own_refund(string staffId, string account) =>
        Case().Approve(staffId, account, "TICKET-1", _now).ShouldBe(RefundCaseRefusal.SelfApproval);

    [Fact]
    public void A_refund_is_approved_once_within_its_window_and_settled_once()
    {
        Case().Approve("staff-2", "account-b", "TICKET-1", _now + RefundCase.ApprovalWindow + TimeSpan.FromSeconds(1)).ShouldBe(RefundCaseRefusal.Expired);

        var refund = Case();
        refund.Approve("staff-2", "account-b", "TICKET-1", _now).ShouldBeNull();
        refund.Approve("staff-3", "account-c", "TICKET-1", _now).ShouldBe(RefundCaseRefusal.AlreadyDecided);
        refund.Reject("staff-1", "TICKET-1", _now).ShouldBe(RefundCaseRefusal.AlreadyDecided);
        refund.Settle(refunded: true, _now).ShouldBeTrue();
        refund.Settle(refunded: false, _now).ShouldBeFalse(); // a redelivered outcome changes nothing
        refund.Status.ShouldBe(RefundCaseStatus.Refunded);
    }

    [Fact]
    public void A_cancellation_with_nothing_to_refund_is_closed_at_once()
    {
        var nothing = RefundCase.Open(Guid.NewGuid(), Guid.NewGuid(), RefundCaseKind.Cancellation, [Guid.NewGuid()], Money(0m), Money(0m), Money(0m),
            "DESK-1", "TICKET-1", "staff-1", "account-a", "key-1", "fingerprint", _now);

        (nothing.Status, nothing.IsOpen, nothing.SettledAt).ShouldBe((RefundCaseStatus.NoRefund, false, _now));
    }

    [Fact]
    public void No_fee_unless_one_is_configured_and_an_expired_pending_case_no_longer_holds_money()
    {
        var options = new RefundOptions();
        options.FeeFor(new CurrencyCode("XTS")).ShouldBe(Money(0m)); // none unless configured (and disclosed)
        options.CancellationFee["XTS"] = 15m;
        options.FeeFor(new CurrencyCode("XTS")).ShouldBe(Money(15m));

        var pending = Case();
        pending.HoldsRefundable(_now + RefundCase.ApprovalWindow).ShouldBeTrue();
        pending.HoldsRefundable(_now + RefundCase.ApprovalWindow + TimeSpan.FromSeconds(1)).ShouldBeFalse();
        pending.Approve("staff-2", "account-b", "TICKET-1", _now).ShouldBeNull();
        pending.HoldsRefundable(_now + TimeSpan.FromDays(30)).ShouldBeTrue(); // approved: held until settled
    }

    [Fact]
    public void Cancelled_items_derive_the_order_status()
    {
        Order.Derive([OrderItemStatus.Cancelled, OrderItemStatus.Confirmed]).ShouldBe(OrderStatus.PartiallyConfirmed);
        Order.Derive([OrderItemStatus.Cancelled, OrderItemStatus.Failed]).ShouldBe(OrderStatus.Cancelled);
        Order.Derive([OrderItemStatus.Cancelled]).ShouldBe(OrderStatus.Cancelled);
    }

    private static Money Money(decimal amount) => new(amount, new CurrencyCode("XTS"));

    private static RefundCase Case() =>
        RefundCase.Open(Guid.NewGuid(), Guid.NewGuid(), RefundCaseKind.Goodwill, [], Money(50m), null, Money(0m), null, "TICKET-1", "staff-1", "account-a",
            "key-1", "fingerprint", _now);

    private Order Confirmed()
    {
        var order = OrderTests.NewOrder();
        order.StartBooking("auth-1", _staff).IsSuccess.ShouldBeTrue();
        order.Confirm(order.Items[0].Id, "mock", "LOC1", _staff).IsSuccess.ShouldBeTrue();
        return order;
    }
}
