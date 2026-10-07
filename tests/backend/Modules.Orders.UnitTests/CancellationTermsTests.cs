using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>
/// A hotel stay's refund on cancellation follows the booked rate's agreed terms (ADR 0030 §7): all of it before the
/// deadline, the price less the penalty after it (the whole price when none is stated), nothing for a non-refundable rate.
/// </summary>
public sealed class CancellationTermsTests
{
    private static readonly DateTimeOffset _deadline = new(2027, 4, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, 450, 120, 450)] // asked before the deadline: everything back
    [InlineData(0, 450, 120, 330)] // at the deadline: no longer free
    [InlineData(60, 450, 120, 330)] // after it: the price less the penalty
    [InlineData(60, 450, 600, 0)] // a penalty above the price takes it all, never below zero
    public void A_refundable_rate_refunds_in_full_before_its_deadline_and_less_the_penalty_after(int minutesAfterDeadline, decimal paid, decimal penalty, decimal refund) =>
        new CancellationTerms(true, _deadline, penalty).RefundOf(paid, _deadline.AddMinutes(minutesAfterDeadline)).ShouldBe(refund);

    [Fact]
    public void After_the_deadline_without_a_stated_penalty_nothing_is_refunded_and_a_non_refundable_rate_refunds_nothing()
    {
        new CancellationTerms(true, _deadline, null).RefundOf(450, _deadline.AddDays(1)).ShouldBe(0);
        new CancellationTerms(true, _deadline, null).RefundOf(450, _deadline.AddDays(-1)).ShouldBe(450);
        CancellationTerms.NonRefundable.RefundOf(450, _deadline.AddDays(-30)).ShouldBe(0);
    }

    [Fact]
    public void A_hotel_order_keeps_the_agreed_terms_and_a_change_before_payment_is_recorded()
    {
        var context = new TransitionContext(OrderTests.Now, "customer", "trace-t");
        var terms = new CancellationTerms(true, _deadline, 120);
        var order = Order.CreateForHotel("cust-1", "key-t", Guid.NewGuid(), OrderTests.Price, OrderTests.Now.AddMinutes(30), null, context,
            new TravellerNeeds(2, 0, 0, false, new DateOnly(2027, 4, 13)), terms);
        var item = order.Items[0].Id;

        order.Items[0].CancellationTerms.ShouldBe(terms);
        order.RefreshOffer(item, OrderTests.Price, OrderTests.Now.AddMinutes(30), null, context, terms: terms).IsSuccess.ShouldBeTrue();
        order.Timeline.Count.ShouldBe(2); // the same terms again: nothing recorded

        // Other terms need the customer's new acceptance (F-53): without a new quote they are refused, never adopted.
        order.RefreshOffer(item, OrderTests.Price, OrderTests.Now.AddMinutes(30), null, context, terms: CancellationTerms.NonRefundable)
            .Error.ShouldBeOfType<OrderTransitionError.PriceNotAccepted>();
        order.Items[0].CancellationTerms.ShouldBe(terms);

        var consent = new PriceConsent(Guid.NewGuid(), OrderTests.Now.AddMinutes(2));
        order.RefreshOffer(item, OrderTests.Price, OrderTests.Now.AddMinutes(30), consent, context, terms: CancellationTerms.NonRefundable).IsSuccess.ShouldBeTrue();
        (order.Items[0].CancellationTerms, order.Items[0].AcceptedPriceQuoteId).ShouldBe((CancellationTerms.NonRefundable, consent.AcceptedPriceQuoteId));
        order.Timeline[^1].Reason.ShouldStartWith("Cancellation terms now non-refundable (quote ");

        // One accepted quote may change the price and the terms together.
        var both = new PriceConsent(Guid.NewGuid(), OrderTests.Now.AddMinutes(3));
        var newPrice = new Money(300m, OrderTests.Price.Currency);
        order.RefreshOffer(item, newPrice, OrderTests.Now.AddMinutes(30), both, context, terms: terms).IsSuccess.ShouldBeTrue();
        (order.Items[0].AgreedPrice, order.Items[0].CancellationTerms, order.Items[0].AcceptedPriceQuoteId).ShouldBe((newPrice, terms, both.AcceptedPriceQuoteId));

        // A flight has no such terms, whatever is passed.
        var flight = OrderTests.NewOrder();
        flight.RefreshOffer(flight.Items[0].Id, OrderTests.Price, OrderTests.Now.AddMinutes(30), null, context, terms: terms).IsSuccess.ShouldBeTrue();
        flight.Items[0].CancellationTerms.ShouldBeNull();
    }
}
