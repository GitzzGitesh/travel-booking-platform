using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Hotels.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Notifications.Application;
using TravelBooking.Modules.Notifications.Infrastructure;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Orders.Infrastructure;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// A hotel stay end to end, over HTTP with a real SQL Server, the mock hotel supplier and the mock payment provider
/// (ADR 0030 §6): the same order, checkout and booking rules as a flight. The guests are named, the stay is revalidated
/// before payment, booked through Hotels after authorization, and charged only once confirmed; an unknown outcome is
/// looked up by our reference, never booked again (F-10, F-11, F-12, F-25).
/// </summary>
public sealed class HotelCheckoutBookingTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task A_hotel_stay_is_ordered_for_its_guests_booked_and_charged_only_after_the_confirmed_booking()
    {
        var token = Token();
        var order = await ReadyOrder(token);

        using var checkout = await Checkout(token, order);

        checkout.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(checkout);
        (body.GetProperty("outcome").GetString(), body.GetProperty("payment").GetString()).ShouldBe(("Booked", "Accepted"));
        var item = body.GetProperty("order").GetProperty("items")[0];
        (item.GetProperty("product").GetString(), item.GetProperty("status").GetString()).ShouldBe(("Hotel", "Confirmed"));
        item.GetProperty("bookingReference").GetString()!.ShouldStartWith("MH");
        item.GetProperty("ticketing").ValueKind.ShouldBe(JsonValueKind.Null); // no tickets for a stay

        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Authorized); // the charge goes through the outbox
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        var payment = await PaymentOf(order);
        (payment.Status, payment.CaptureAmount).ShouldBe((PaymentAttemptStatus.Captured, payment.Amount));
    }

    [Fact]
    public async Task A_hotel_booking_that_timed_out_but_was_made_is_found_by_reconciliation_then_charged_never_booked_again()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockHotelBookingScenarios.TimeoutBookedSurname);

        using var checkout = await Checkout(token, order);

        checkout.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("BookingPending");
        (await LoadOrder(order)).Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation);
        // Frozen while the booking is unresolved: a price check can never change what is being booked.
        var selection = (await LoadOrder(order)).Items[0].SelectedOfferId;
        using (var check = await Send(HttpMethod.Post, $"/api/v1/hotels/selected-offers/{selection}/revalidations", token))
        {
            check.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }


        await Run(ReconcileBookingsJob.Name);
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);

        var stored = await LoadOrder(order);
        (stored.Status, stored.Items[0].Product).ShouldBe((OrderStatus.Confirmed, OrderProduct.Hotel));
        stored.Items[0].SupplierLocator!.ShouldStartWith("MH");
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Captured);
    }

    [Fact]
    public async Task A_refused_hotel_booking_fails_and_releases_the_hold()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockHotelBookingScenarios.RejectedSurname);

        using var checkout = await Checkout(token, order);

        (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("BookingFailed");
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        var payment = await PaymentOf(order);
        (payment.Status, payment.CaptureRequestedAt).ShouldBe((PaymentAttemptStatus.Voided, null));
    }

    // F-02 at checkout: the stay is revalidated before any payment; an expired offer is never authorized or booked.
    [Fact]
    public async Task An_offer_that_expired_before_payment_is_never_authorized()
    {
        var token = Token();
        var order = await ReadyOrder(token);
        api.Clock.Advance(TimeSpan.FromMinutes(31)); // mock offers expire 30 minutes after the search

        using var checkout = await Checkout(token, order);

        checkout.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountPayments(order)).ShouldBe(0);
        (await LoadOrder(order)).Items[0].Status.ShouldNotBe(FlightOrderItemStatus.Booking);
    }

    // Duplicate and parallel checkouts (testing rules): one authorization, one booking, one charge.
    [Fact]
    public async Task A_repeated_or_parallel_hotel_checkout_books_once_and_charges_once()
    {
        var token = Token();
        var order = await ReadyOrder(token);
        var key = $"pay-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Checkout(token, order, key)));
        using var replay = await Checkout(token, order, key);

        foreach (var response in responses)
        {
            ((int)response.StatusCode).ShouldBeOneOf(200, 202, 409, 503);
            response.Dispose();
        }

        (await Read(replay)).GetProperty("outcome").GetString().ShouldBe("Booked");
        (await CountPayments(order)).ShouldBe(1);
        var stored = await LoadOrder(order);
        (stored.Status, stored.Items.Count).ShouldBe((OrderStatus.Confirmed, 1));
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Captured);
    }

    // ADR 0030 §7: a cancelled stay is refunded by the booked rate's agreed terms, not by what the supplier's desk returns,
    // with no cancellation fee (free cancellation was advertised); a second person approves it as for any refund.
    [Fact]
    public async Task A_stay_cancelled_before_its_deadline_is_refunded_in_full_whatever_the_desk_returns()
    {
        var token = Token();
        var (order, price) = await CapturedOrder(token);
        (await LoadOrder(order)).Items[0].CancellationTerms.ShouldNotBeNull().Refundable.ShouldBeTrue();
        using var mine = await Send(HttpMethod.Get, $"/api/v1/orders/{order}", token);
        var terms = (await Read(mine)).GetProperty("items")[0].GetProperty("cancellation");
        (terms.GetProperty("refundable").GetBoolean(), terms.GetProperty("freeCancellationUntil").ValueKind).ShouldBe((true, JsonValueKind.String));
        (await Send(HttpMethod.Post, $"/api/v1/orders/{order}/cancellation-requests", token, key: $"cxl-{Guid.NewGuid():N}")).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        var refundCase = await RecordCancellation(order, supplierRefund: "1");

        refundCase.GetProperty("amount").GetProperty("amount").GetString().ShouldBe(price.Amount.ToString(CultureInfo.InvariantCulture));
        (refundCase.GetProperty("fee").GetString(), refundCase.GetProperty("status").GetString()).ShouldBe(("0", "PendingApproval"));
        var stored = await LoadOrder(order);
        stored.Items[0].Status.ShouldBe(FlightOrderItemStatus.Cancelled);
        stored.Timeline.ShouldContain(e => e.Reason.Contains("by the rate's terms (free cancellation until", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_non_refundable_stay_is_cancelled_with_nothing_to_refund()
    {
        var token = Token();
        var (order, _) = await CapturedOrder(token, offerIndex: 1); // the mock's non-refundable rate

        var refundCase = await RecordCancellation(order, supplierRefund: "0");

        (refundCase.GetProperty("amount").GetProperty("amount").GetString(), refundCase.GetProperty("status").GetString()).ShouldBe(("0", "NoRefund"));
        (await LoadOrder(order)).Items[0].Status.ShouldBe(FlightOrderItemStatus.Cancelled);
    }

    // The deadline is judged when the customer asked (their open request), so a late desk never costs them: we refund in
    // full and the shortfall against the supplier is noted. Without a request, the penalty after the deadline applies.
    // The mock's deadline is noon UTC two days before check-in (40 days ahead), so 39 days later it has passed.
    [Fact]
    public async Task The_deadline_is_judged_when_the_customer_asked_and_without_a_request_the_penalty_applies()
    {
        var asking = Token();
        var (askedInTime, price) = await CapturedOrder(asking);
        (await Send(HttpMethod.Post, $"/api/v1/orders/{askedInTime}/cancellation-requests", asking, key: $"cxl-{Guid.NewGuid():N}")).Dispose();
        var (notAsked, _) = await CapturedOrder(Token());
        var penalty = (await LoadOrder(notAsked)).Items[0].CancellationTerms!.PenaltyAmount!.Value;

        api.Clock.Advance(TimeSpan.FromDays(39));
        var late = await RecordCancellation(askedInTime, supplierRefund: "0"); // the supplier kept everything
        var afterDeadline = await RecordCancellation(notAsked, supplierRefund: "0");

        late.GetProperty("amount").GetProperty("amount").GetString().ShouldBe(price.Amount.ToString(CultureInfo.InvariantCulture));
        (await LoadOrder(askedInTime)).Timeline.ShouldContain(e => e.Reason.Contains("shortfall", StringComparison.Ordinal));
        afterDeadline.GetProperty("amount").GetProperty("amount").GetString()
            .ShouldBe((price.Amount - penalty).ToString(CultureInfo.InvariantCulture));
    }

    // A cancellation by the hotel or the supplier (a walk, a closure): the customer gets at least what the supplier returns.
    [Fact]
    public async Task A_non_refundable_stay_the_supplier_refunds_in_full_is_refunded_in_full()
    {
        var (order, price) = await CapturedOrder(Token(), offerIndex: 1);

        var refundCase = await RecordCancellation(order, supplierRefund: price.Amount.ToString(CultureInfo.InvariantCulture));

        refundCase.GetProperty("amount").GetProperty("amount").GetString().ShouldBe(price.Amount.ToString(CultureInfo.InvariantCulture));
        refundCase.GetProperty("status").GetString().ShouldBe("PendingApproval"); // a second person still approves
    }

    // The voucher (ADR 0030): the confirmation email states the stay as booked; the booked selection is frozen, and
    // operations see the stay with the agreed terms. Never guest data in the email.
    [Fact]
    public async Task A_booked_stay_is_frozen_emailed_as_a_voucher_and_shown_to_operations()
    {
        var token = Token();
        var (order, _) = await CapturedOrder(token);
        var selection = (await LoadOrder(order)).Items[0].SelectedOfferId;

        using var revalidate = await Send(HttpMethod.Post, $"/api/v1/hotels/selected-offers/{selection}/revalidations", token);
        revalidate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Read(revalidate)).GetProperty("type").GetString().ShouldBe("selection-booked");

        await Run("orders.outbox");
        await Run(SendNotificationsJob.Name);
        var email = api.Services.GetRequiredService<RecordingEmailSender>().Sent
            .Single(m => m.TextBody.Contains(order.ToString(), StringComparison.Ordinal));
        email.Subject.ShouldBe("Your booking is confirmed");
        email.TextBody.ShouldContain("Hotel: Mock Central Hotel, 1 Mock Street");
        email.TextBody.ShouldContain("(3 nights)");
        email.TextBody.ShouldContain("Room: Double room, breakfast included");
        email.TextBody.ShouldContain("Cancellation: Free cancellation if you ask before");
        email.TextBody.ShouldNotContain("Lovelace"); // no guest names
        email.HtmlBody.ShouldContain("Mock Central Hotel");

        using var detail = await Send(HttpMethod.Get, $"/api/admin/v1/orders/{order}", TestStaffTokens.For(TestStaffTokens.Operations));
        var body = await Read(detail);
        body.GetProperty("order").GetProperty("items")[0].GetProperty("product").GetString().ShouldBe("Hotel");
        var stay = body.GetProperty("stays").EnumerateArray().ShouldHaveSingleItem();
        (stay.GetProperty("hotel").GetString(), stay.GetProperty("nights").GetInt32(), stay.GetProperty("booked").GetBoolean())
            .ShouldBe(("Mock Central Hotel", 3, true));
        stay.GetProperty("agreedCancellation").GetProperty("refundable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_selection_ordered_as_another_product_is_not_found_and_an_unknown_product_is_refused()
    {
        var token = Token();
        var selection = await ConfirmedSelection(token);

        using var asFlight = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selection }, $"order-{Guid.NewGuid():N}");
        using var asTrain = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selection, product = "Train" }, $"order-{Guid.NewGuid():N}");

        asFlight.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // a hotel selection is not a flight's: not found, never ordered
        asTrain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Token() => TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    // Two adults and a child aged 8 at check-out, in 40 days.
    private DateOnly CheckIn => DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(40);

    private async Task<Guid> ConfirmedSelection(string token, int offerIndex = 0)
    {
        using var search = await Send(HttpMethod.Post, "/api/v1/hotels/searches", token, new
        {
            destination = "PAR",
            checkIn = CheckIn.ToString("yyyy-MM-dd"),
            checkOut = CheckIn.AddDays(3).ToString("yyyy-MM-dd"),
            adults = 2,
            childAges = new[] { 8 },
        });
        var found = await Read(search);
        var searchId = found.GetProperty("searchId").GetGuid();
        var offerId = found.GetProperty("offers")[offerIndex].GetProperty("offerId").GetGuid();

        using var select = await Send(HttpMethod.Post, "/api/v1/hotels/selected-offers", token, new { searchId, offerId });
        var selected = (await Read(select)).GetProperty("selectedOfferId").GetGuid();
        using var revalidated = await Send(HttpMethod.Post, $"/api/v1/hotels/selected-offers/{selected}/revalidations", token);
        revalidated.StatusCode.ShouldBe(HttpStatusCode.OK);
        return selected;
    }

    // Search, select, revalidate, order as a hotel stay, and name its guests with the contact: ready to pay.
    private async Task<Guid> ReadyOrder(string token, string leadSurname = "Lovelace", int offerIndex = 0)
    {
        var selection = await ConfirmedSelection(token, offerIndex);
        using var created = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selection, product = "Hotel" }, $"order-{Guid.NewGuid():N}");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await Read(created);
        var needs = body.GetProperty("items")[0].GetProperty("travellers");
        (needs.GetProperty("adults").GetInt32(), needs.GetProperty("children").GetInt32(), needs.GetProperty("documentsRequired").GetBoolean()).ShouldBe((2, 1, false));
        var orderId = body.GetProperty("orderId").GetGuid();

        var childBirth = CheckIn.AddDays(3).AddYears(-8).AddDays(-10).ToString("yyyy-MM-dd");
        using var travellers = await Send(HttpMethod.Put, $"/api/v1/orders/{orderId}/travellers", token, new
        {
            contact = new { email = "ada@example.com", phone = "+447700900123" },
            travellers = new object[]
            {
                new { type = "Child", givenNames = "Allegra", surname = "Byron", dateOfBirth = childBirth, gender = "Female" },
                new { type = "Adult", givenNames = "Ada", surname = leadSurname, dateOfBirth = "1990-12-10", gender = "Female" },
                new { type = "Adult", givenNames = "George", surname = "Byron", dateOfBirth = "1988-01-22", gender = "Male" },
            },
        });
        travellers.StatusCode.ShouldBe(HttpStatusCode.OK);
        return orderId;
    }

    private async Task<(Guid Order, Money Price)> CapturedOrder(string token, int offerIndex = 0)
    {
        var order = await ReadyOrder(token, offerIndex: offerIndex);
        (await Checkout(token, order)).Dispose();
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Captured);
        return (order, (await LoadOrder(order)).Items[0].AgreedPrice);
    }

    // Operations cancel at the supplier's desk and record it (ADR 0027): the refund case, with its computed amount.
    private async Task<JsonElement> RecordCancellation(Guid order, string supplierRefund)
    {
        var item = (await LoadOrder(order)).Items[0].Id;
        using var recorded = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", TestStaffTokens.For(TestStaffTokens.Operations), new
        {
            kind = "Cancellation",
            itemIds = new[] { item },
            supplierReference = "DESK-HTL-1",
            supplierRefund,
            reason = "TICKET-801",
        }, $"case-{Guid.NewGuid():N}");
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await Read(recorded);
    }

    private Task<HttpResponseMessage> Checkout(string token, Guid orderId, string? key = null) =>
        Send(HttpMethod.Post, $"/api/v1/orders/{orderId}/checkout", token, new { paymentMethodToken = MockPaymentMethods.Approved }, key ?? $"pay-{Guid.NewGuid():N}");

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body = null, string? key = null)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task Run(string job)
    {
        var schedule = api.Services.GetServices<BackgroundJobSchedule>().Single(s => s.Name == job);
        using var scope = api.Services.CreateScope();
        await ((IBackgroundJob)scope.ServiceProvider.GetRequiredService(schedule.JobType)).RunOnceAsync(Ct);
    }

    private async Task<Order> LoadOrder(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders.AsNoTracking()
            .Include(o => o.Items).Include(o => o.Timeline).AsSplitQuery().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task<PaymentAttempt> PaymentOf(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking().SingleAsync(p => p.OrderId == orderId, Ct);
    }

    private async Task<int> CountPayments(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.CountAsync(p => p.OrderId == orderId, Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
}
