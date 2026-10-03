using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Notifications.Application;
using TravelBooking.Modules.Notifications.Domain;
using TravelBooking.Modules.Notifications.Infrastructure;
using TravelBooking.Modules.Notifications.Ports;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Orders.Infrastructure;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The first bookable flight path end to end, over HTTP with a real SQL Server, the mock supplier and the mock payment
/// provider (ADR 0005, ADR 0021): travellers complete → revalidate → authorize → book → capture only after a confirmed
/// booking. Unknown booking outcomes are looked up by the Worker's reconciliation, never booked again; a booking that
/// failed releases the hold; nothing is charged twice (F-10, F-11, F-12, F-23, F-25, F-30, F-32).
/// </summary>
public sealed class CheckoutBookingTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task A_complete_checkout_books_the_flight_and_charges_only_after_the_booking_is_confirmed()
    {
        var token = Token();
        var order = await ReadyOrder(token);

        using var checkout = await Checkout(token, order, NewKey());

        checkout.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(checkout);
        (body.GetProperty("outcome").GetString(), body.GetProperty("payment").GetString()).ShouldBe(("Booked", "Accepted"));
        var item = body.GetProperty("order").GetProperty("items")[0];
        (item.GetProperty("status").GetString(), item.GetProperty("ticketing").GetString()).ShouldBe(("Confirmed", "Issued"));
        item.GetProperty("bookingReference").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("order").GetProperty("status").GetString().ShouldBe("Confirmed");

        var payment = await PaymentOf(order);
        payment.Status.ShouldBe(PaymentAttemptStatus.Authorized); // not charged yet: the charge goes through the outbox
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);

        payment = await PaymentOf(order);
        (payment.Status, payment.CaptureAmount).ShouldBe((PaymentAttemptStatus.Captured, payment.Amount));
    }

    [Fact]
    public async Task A_repeated_or_parallel_checkout_books_once_and_charges_once()
    {
        var token = Token();
        var order = await ReadyOrder(token);
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Checkout(token, order, key)));
        using var replay = await Checkout(token, order, key);

        // Each answer is the outcome, or "try again", or (a duplicate arriving while the first is still paying) 409
        // payment-in-progress: never a second effect. Anything else is reported with its problem type.
        foreach (var response in responses)
        {
            var answer = response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable
                ? $"{(int)response.StatusCode} {(await Problem(response)).Item2}"
                : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            answer.ShouldBeOneOf("200", "202", "503 try-again", "409 payment-in-progress");
        }

        (await Read(replay)).GetProperty("outcome").GetString().ShouldBe("Booked");
        var stored = await LoadOrder(order);
        stored.Timeline.Count(e => e is { FromStatus: "Booking", ToStatus: "Confirmed" }).ShouldBe(1); // one booking transition
        (await CountOutbox(order, "OrderPaymentCaptureRequested")).ShouldBe(1);
        (await CountPayments(order)).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // An order is read by separate queries (order, items, timeline). A change committed between them must never give a
    // torn order (items from after the change on an order row from before it), which made a duplicate checkout answer
    // order-not-payable: the store loads it again.
    [Fact]
    public async Task An_order_changed_while_it_is_being_loaded_is_loaded_again_never_torn()
    {
        var orderId = await ReadyOrder(Token());
        var lookupAt = api.Clock.GetUtcNow().AddMinutes(5);
        var interceptor = new CommitBeforeItemsQuery(async () =>
        {
            using var scope = api.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IOrderStore>();
            var order = (await store.FindAsync(orderId, Ct))!;
            order.RecordBookingLookup(order.Items[0].Id, api.Clock.GetUtcNow(), lookupAt);
            (await store.TrySaveAsync(Ct)).ShouldBeTrue();
        });
        using var services = api.Services.CreateScope();
        var options = services.ServiceProvider.GetRequiredService<DbContextOptions<OrdersDbContext>>();
        await using var db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>(options).AddInterceptors(interceptor).Options);

        var loaded = (await new SqlOrderStore(db).FindAsync(orderId, Ct))!;

        interceptor.Fired.ShouldBeTrue();
        var stored = await LoadOrder(orderId);
        (loaded.Revision, loaded.Items[0].NextBookingLookupAt).ShouldBe((stored.Revision, lookupAt)); // all from after the change
    }

    [Fact]
    public async Task An_order_lookup_by_idempotency_key_is_never_torn_either_and_a_load_that_never_settles_gives_up()
    {
        var orderId = await ReadyOrder(Token());
        var stored = await LoadOrder(orderId);
        var changes = 0;
        async Task Change()
        {
            changes++;
            using var scope = api.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IOrderStore>();
            var order = (await store.FindAsync(orderId, Ct))!;
            order.RecordBookingLookup(order.Items[0].Id, api.Clock.GetUtcNow(), api.Clock.GetUtcNow().AddMinutes(changes));
            (await store.TrySaveAsync(Ct)).ShouldBeTrue();
        }

        using var services = api.Services.CreateScope();
        var options = services.ServiceProvider.GetRequiredService<DbContextOptions<OrdersDbContext>>();
        var once = new CommitBeforeItemsQuery(Change);
        await using (var db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>(options).AddInterceptors(once).Options))
        {
            var loaded = (await new SqlOrderStore(db).FindByIdempotencyKeyAsync(stored.CustomerId, stored.IdempotencyKey, Ct))!;
            (loaded.Revision, loaded.Items[0].NextBookingLookupAt).ShouldBe(((await LoadOrder(orderId)).Revision, api.Clock.GetUtcNow().AddMinutes(1)));
        }

        var always = new CommitBeforeItemsQuery(Change, everyTime: true);
        await using (var db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>(options).AddInterceptors(always).Options))
        {
            (await Should.ThrowAsync<OrderKeptChangingException>(() => new SqlOrderStore(db).FindAsync(orderId, Ct))).OrderId.ShouldBe(orderId);
        }

        changes.ShouldBe(1 + SqlOrderStore.MaxLoadAttempts);
    }

    // Commits a change just before an items query runs (after the order row was read, before its items): once, or every time.
    private sealed class CommitBeforeItemsQuery(Func<Task> change, bool everyTime = false) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if ((everyTime || !Fired) && command.CommandText.Contains("[FlightOrderItems]", StringComparison.Ordinal))
            {
                Fired = true;
                await change();
            }

            return result;
        }
    }

    // ADR 0024: the customer is told the outcome once, by email, from the settled order: the booking reference and the
    // amount charged, sent through the outbox and the Worker; repeating the jobs never sends it again.
    [Fact]
    public async Task A_confirmed_booking_sends_one_confirmation_with_its_reference_and_charge()
    {
        var token = Token();
        var order = await ReadyOrder(token, email: $"booker-{Guid.NewGuid():N}@example.com");
        (await Checkout(token, order, NewKey())).Dispose();

        await Run("orders.outbox");
        await Run("orders.outbox");
        await Run(SendNotificationsJob.Name);
        await Run(SendNotificationsJob.Name);

        var stored = await LoadOrder(order);
        var email = EmailsAbout(order).ShouldHaveSingleItem();
        email.Subject.ShouldBe("Your booking is confirmed");
        email.To.ShouldStartWith("booker-");
        email.TextBody.ShouldContain(stored.Items[0].SupplierLocator!);
        email.TextBody.ShouldContain($"Charged: {stored.Items[0].AgreedPrice.Amount} {stored.Items[0].AgreedPrice.Currency.Value}");
        // The travellers' names are not in the notice (the surname, matched exactly: a GUID can contain "ada").
        email.HtmlBody.ShouldNotContain("Lovelace", Case.Sensitive);
        email.TextBody.ShouldNotContain("Lovelace", Case.Sensitive);
        var notice = await NoticeFor(order);
        (notice.Kind, notice.Status, notice.Attempts).ShouldBe((NoticeTemplates.BookingConfirmed, NotificationStatus.Accepted, 1));
        notice.Values.ShouldNotContain("@"); // no address stored in the notifications schema
    }

    [Fact]
    public async Task A_failed_booking_tells_the_customer_nothing_was_charged()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockBookingScenarios.RejectedFamilyName);
        (await Checkout(token, order, NewKey())).Dispose();

        await Run("orders.outbox");
        await Run(SendNotificationsJob.Name);

        var email = EmailsAbout(order).ShouldHaveSingleItem();
        email.Subject.ShouldBe("We could not complete your booking");
        email.TextBody.ShouldContain("Charged: Nothing");
    }

    // At least once, never twice (ADR 0024): a redelivered event finds the unique (event, kind) record and adds nothing; an
    // order with no contact any more (anonymised, or never given) is suppressed, and nothing is sent anywhere.
    [Fact]
    public async Task A_redelivered_event_records_one_notice_and_an_order_without_a_contact_is_suppressed()
    {
        var orderId = Guid.NewGuid(); // no travellers or contact were ever stored for it
        var settled = new OrderBookingSettled(Guid.NewGuid(), api.Clock.GetUtcNow(), orderId, BookingOutcome.NotBooked, [], null, "trace-1");
        foreach (var _ in new[] { 1, 2 })
        {
            using var scope = api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<OrderBookingSettled>>().HandleAsync(settled, Ct);
        }

        await Run(SendNotificationsJob.Name);

        var notice = await NoticeFor(orderId);
        (notice.Status, notice.LastError, notice.Attempts).ShouldBe((NotificationStatus.Suppressed, "no-contact", 0));
        EmailsAbout(orderId).ShouldBeEmpty();
    }

    // The provider's answer decides: unknown → sent again later (backoff), refused → failed for good, a suppressed address →
    // suppressed.
    [Theory]
    [InlineData(RecordingEmailSender.UnknownDomain, "Pending")]
    [InlineData(RecordingEmailSender.RejectedDomain, "Failed")]
    [InlineData(RecordingEmailSender.SuppressedDomain, "Suppressed")]
    public async Task The_providers_answer_decides_what_happens_to_a_notice(string domain, string expected)
    {
        var token = Token();
        var order = await ReadyOrder(token, email: $"booker@{domain}");
        (await Checkout(token, order, NewKey())).Dispose();

        await Run("orders.outbox");
        await Run(SendNotificationsJob.Name);
        await Run(SendNotificationsJob.Name); // a retry is not due yet

        var notice = await NoticeFor(order);
        (notice.Status.ToString(), notice.Attempts).ShouldBe((expected, 1));
        EmailsAbout(order).ShouldBeEmpty();
    }

    // ---------- Cancellations and refunds (ADR 0027) ----------

    [Fact]
    public async Task A_cancellation_is_refunded_once_after_a_second_person_approves_it_and_the_customer_is_told()
    {
        var (order, price) = await CapturedOrder();
        var item = (await LoadOrder(order)).Items[0].Id;

        using var opened = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations), new
        {
            kind = "Cancellation",
            itemIds = new[] { item },
            supplierReference = "DESK-CXL-9",
            supplierRefund = price.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason = "TICKET-501",
        }, NewKey());
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        var refundCase = await Read(opened);
        var caseId = refundCase.GetProperty("caseId").GetGuid();
        (refundCase.GetProperty("status").GetString(), refundCase.GetProperty("amount").GetProperty("amount").GetString())
            .ShouldBe(("PendingApproval", price.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))); // computed by the server
        (await LoadOrder(order)).Items[0].Status.ShouldBe(FlightOrderItemStatus.Cancelled); // the cancellation is a fact at once
        await Run("orders.outbox");
        (await CountRefunds(order)).ShouldBe(0); // nothing refunded before approval

        using var byOpener = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", Staff(TestStaffTokens.Operations), new { approve = true, reason = "TICKET-501" });
        byOpener.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // Operations opens; it never approves
        using var approved = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", Staff(TestStaffTokens.Finance), new { approve = true, reason = "TICKET-501 checked" });
        (await Read(approved)).GetProperty("status").GetString().ShouldBe("Approved");

        await Run("orders.outbox");
        await Run("orders.outbox");
        await Run(ExecuteRefundsJob.Name);
        await Run(ExecuteRefundsJob.Name);
        await Run("payments.outbox");
        await Run(SendNotificationsJob.Name);

        (await CountRefunds(order)).ShouldBe(1); // once
        using var cases = await Send(HttpMethod.Get, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations));
        (await Read(cases)).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Refunded");
        var email = EmailsAbout(order).Single(m => m.Subject == "Your refund has been sent");
        email.TextBody.ShouldContain($"Refunded: {price.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {price.Currency.Value}");
        var cancelled = EmailsAbout(order).Single(m => m.Subject == "Your booking has been cancelled"); // once, when the cancellation was recorded
        cancelled.TextBody.ShouldContain("If a refund is due");
        cancelled.TextBody.ShouldNotContain(price.Currency.Value); // no amount promised before a second person approves it
    }

    [Fact]
    public async Task A_refund_that_failed_is_told_to_the_customer_as_delayed_never_as_sent()
    {
        var (order, price) = await CapturedOrder();
        var settled = new TravelBooking.Modules.Payments.Contracts.PaymentRefundSettled(Guid.NewGuid(), api.Clock.GetUtcNow(), order, Guid.NewGuid(), Guid.NewGuid(),
            new Money(10m, price.Currency), Succeeded: false, "trace-refund-failed");
        using (var scope = api.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IIntegrationEventHandler<TravelBooking.Modules.Payments.Contracts.PaymentRefundSettled>>()
                .Single(h => h is TravelBooking.Modules.Notifications.Application.PaymentRefundSettledHandler);
            await handler.HandleAsync(settled, Ct);
            await handler.HandleAsync(settled, Ct); // delivered twice: one notice
        }

        await Run(SendNotificationsJob.Name);

        EmailsAbout(order).ShouldNotContain(m => m.Subject == "Your refund has been sent");
        var delayed = EmailsAbout(order).Single(m => m.Subject == "Your refund is delayed").TextBody;
        delayed.ShouldContain($"Refund amount: 10 {price.Currency.Value}");
        delayed.ShouldNotContain("Refunded:");
    }

    [Fact]
    public async Task A_refund_in_review_is_checked_with_the_provider_in_parallel_safely_and_never_sent_again()
    {
        var (order, _) = await CapturedOrder(MockPaymentMethods.RefundPending);
        var refundId = await ApprovedGoodwill(order, "10");
        await Run(ExecuteRefundsJob.Name); // sent once: pending at the provider
        api.Clock.Advance(RefundRecord.UnresolvedAfter);
        await Run(ExecuteRefundsJob.Name); // still pending after its limit: a person decides
        (await RefundOf(refundId)).Status.ShouldBe(RefundRecordStatus.ManualReview);

        var url = $"/api/admin/v1/payments/refunds/{refundId}/review-resolutions";
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), new { reason = "TICKET-601" })));
        foreach (var response in responses)
        {
            using (response)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                var body = await Read(response);
                (body.GetProperty("resolved").GetBoolean(), body.GetProperty("status").GetString()).ShouldBe((false, "ManualReview"));
            }
        }

        using var missing = await Send(HttpMethod.Post, $"/api/admin/v1/payments/refunds/{Guid.NewGuid()}/review-resolutions",
            Staff(TestStaffTokens.Operations), new { reason = "TICKET-601" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RefundOf(refundId)).Status.ShouldBe(RefundRecordStatus.ManualReview); // the provider still says pending
        (await CountRefunds(order)).ShouldBe(1);
    }

    [Fact]
    public async Task A_refund_that_is_not_in_review_says_where_it_stands()
    {
        var (order, _) = await CapturedOrder();
        var refundId = await ApprovedGoodwill(order, "10");
        await Run(ExecuteRefundsJob.Name);

        using var settled = await Send(HttpMethod.Post, $"/api/admin/v1/payments/refunds/{refundId}/review-resolutions",
            Staff(TestStaffTokens.Operations), new { reason = "TICKET-602" });

        settled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await Read(settled);
        (problem.GetProperty("type").GetString(), problem.GetProperty("refundStatus").GetString()).ShouldBe(("not-in-review", "Succeeded"));
    }

    // A goodwill case opened by Operations and approved by Finance, delivered to Payments: its id is the refund's.
    private async Task<Guid> ApprovedGoodwill(Guid order, string amount)
    {
        using var opened = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations),
            new { kind = "Goodwill", amount, reason = "TICKET-600" }, NewKey());
        var caseId = (await Read(opened)).GetProperty("caseId").GetGuid();
        using var approved = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", Staff(TestStaffTokens.Finance),
            new { approve = true, reason = "TICKET-600 checked" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Run("orders.outbox");
        return caseId;
    }

    private async Task<RefundRecord> RefundOf(Guid refundId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<RefundRecord>().AsNoTracking().SingleAsync(r => r.Id == refundId, Ct);
    }

    [Fact]
    public async Task A_goodwill_refund_never_exceeds_what_can_be_refunded_and_its_opener_cannot_approve_it()
    {
        var (order, price) = await CapturedOrder();
        var url = $"/api/admin/v1/orders/{order}/refund-cases";

        using var tooMuch = await Send(HttpMethod.Post, url, Staff(TestStaffTokens.Administrator),
            new { kind = "Goodwill", amount = (price.Amount + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), reason = "TICKET-502" }, NewKey());
        using var goodwill = await Send(HttpMethod.Post, url, Staff(TestStaffTokens.Administrator), new { kind = "Goodwill", amount = "10", reason = "TICKET-502" }, NewKey());
        var caseId = (await Read(goodwill)).GetProperty("caseId").GetGuid();
        using var self = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", Staff(TestStaffTokens.Administrator), new { approve = true, reason = "TICKET-502" });
        using var otherWithdraws = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/withdrawal", Staff(TestStaffTokens.Operations), new { reason = "TICKET-502" });

        (await Problem(tooMuch)).ShouldBe((HttpStatusCode.Conflict, "exceeds-refundable"));
        (await Problem(self)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));
        (await Problem(otherWithdraws)).ShouldBe((HttpStatusCode.Forbidden, "withdrawal-requester-only"));
    }

    [Fact]
    public async Task Nothing_is_refunded_for_an_order_that_was_never_charged()
    {
        var order = await ReadyOrder(Token());

        using var refused = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations),
            new { kind = "Goodwill", amount = "10", reason = "TICKET-503" }, NewKey());

        (await Problem(refused)).ShouldBe((HttpStatusCode.Conflict, "not-captured"));
    }

    // Idempotency (non-negotiable 4): one case per requester and key, also for requests sent at the same moment; the
    // same key for another request is refused. Without a key nothing is opened.
    [Fact]
    public async Task A_repeated_refund_request_opens_one_case_and_a_reused_key_is_refused()
    {
        var (order, _) = await CapturedOrder();
        var url = $"/api/admin/v1/orders/{order}/refund-cases";
        var key = NewKey();
        var goodwill = new { kind = "Goodwill", amount = "10", reason = "TICKET-504" };

        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), goodwill, key)));
        using var replay = await Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), goodwill, key);
        using var reused = await Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), new { kind = "Goodwill", amount = "11", reason = "TICKET-504" }, key);
        using var withoutKey = await Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), goodwill);

        parallel.Select(r => r.StatusCode).ShouldAllBe(code => code == HttpStatusCode.Created || code == HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        parallel.Select(r => Read(r).Result.GetProperty("caseId").GetGuid()).Distinct().ShouldHaveSingleItem()
            .ShouldBe((await Read(replay)).GetProperty("caseId").GetGuid());
        (await Problem(reused)).ShouldBe((HttpStatusCode.Conflict, "idempotency-conflict"));
        withoutKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreach (var response in parallel)
        {
            response.Dispose();
        }
    }

    // Concurrency (testing rules): approvals at the same moment request one refund; refunds that together exceed the
    // charge never refund more than it; one item cancelled twice at once is one case.
    [Fact]
    public async Task Parallel_approvals_refund_once_and_parallel_refunds_never_exceed_the_charge()
    {
        var (order, price) = await CapturedOrder();
        var url = $"/api/admin/v1/orders/{order}/refund-cases";
        var share = decimal.Round(price.Amount * 0.6m, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var opened = await Task.WhenAll(
            Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), new { kind = "Goodwill", amount = share, reason = "TICKET-505" }, NewKey()),
            Send(HttpMethod.Post, url, Staff(TestStaffTokens.Operations), new { kind = "Goodwill", amount = share, reason = "TICKET-505" }, NewKey()));
        var caseIds = opened.Where(r => r.StatusCode == HttpStatusCode.Created).Select(r => Read(r).Result.GetProperty("caseId").GetGuid()).ToList();
        caseIds.ShouldNotBeEmpty();

        foreach (var caseId in caseIds)
        {
            var decisions = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", Staff(TestStaffTokens.Finance), new { approve = true, reason = "TICKET-505" })));
            decisions.Count(d => d.StatusCode == HttpStatusCode.OK).ShouldBe(1); // the others: already decided, or a conflict
        }

        await Run("orders.outbox");
        await Run(ExecuteRefundsJob.Name);
        await Run("payments.outbox");
        await Run("orders.outbox");

        (await CountOutbox(order, "OrderRefundRequested")).ShouldBe(caseIds.Count); // one per approved case
        using var scope = api.Services.CreateScope();
        var refunds = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<RefundRecord>().AsNoTracking()
            .Where(r => r.OrderId == order).ToListAsync(Ct);
        refunds.Count.ShouldBe(caseIds.Count);
        refunds.Where(r => r.Status != RefundRecordStatus.Failed).Sum(r => r.AmountValue).ShouldBeLessThanOrEqualTo(price.Amount);
        foreach (var response in opened)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task One_item_cancelled_twice_at_once_opens_one_case()
    {
        var (order, price) = await CapturedOrder();
        var item = (await LoadOrder(order)).Items[0].Id;
        var cancel = new
        {
            kind = "Cancellation",
            itemIds = new[] { item },
            supplierReference = "DESK-CXL-10",
            supplierRefund = price.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason = "TICKET-506",
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations), cancel, NewKey())));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        using var cases = await Send(HttpMethod.Get, $"/api/admin/v1/orders/{order}/refund-cases", Staff(TestStaffTokens.Operations));
        (await Read(cases)).GetArrayLength().ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private async Task<(Guid Order, Money Price)> CapturedOrder(string paymentMethod = MockPaymentMethods.Approved)
    {
        var token = Token();
        var order = await ReadyOrder(token, email: $"booker-{Guid.NewGuid():N}@example.com");
        (await Checkout(token, order, NewKey(), paymentMethod)).Dispose();
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Captured);
        return (order, (await LoadOrder(order)).Items[0].AgreedPrice);
    }

    private static string Staff(string objectId) => TestStaffTokens.For(objectId);

    private async Task<int> CountRefunds(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<RefundRecord>().CountAsync(r => r.OrderId == orderId, Ct);
    }

    [Fact]
    public async Task A_supplier_refusal_charges_nothing_and_releases_the_hold()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockBookingScenarios.RejectedFamilyName);

        using var checkout = await Checkout(token, order, NewKey());

        var body = await Read(checkout);
        (body.GetProperty("outcome").GetString(), body.GetProperty("payment").GetString()).ShouldBe(("BookingFailed", "Released"));
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);
        var payment = await PaymentOf(order);
        payment.Status.ShouldBe(PaymentAttemptStatus.Voided);
        payment.CaptureRequestedAt.ShouldBeNull();
    }

    [Fact]
    public async Task F11_a_booking_that_timed_out_but_was_made_is_found_by_reconciliation_then_charged_never_booked_again()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockBookingScenarios.TimeoutBookedFamilyName);

        using var checkout = await Checkout(token, order, NewKey());

        checkout.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("BookingPending");
        (await LoadOrder(order)).Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation);
        (await CountOutbox(order, "OrderPaymentCaptureRequested")).ShouldBe(0); // never charged on an unknown booking

        await Run(ReconcileBookingsJob.Name);
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);

        var stored = await LoadOrder(order);
        stored.Status.ShouldBe(OrderStatus.Confirmed);
        stored.Timeline.Count(e => e.ToStatus == "Booking").ShouldBe(1);
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Captured);
    }

    [Fact]
    public async Task F12_a_booking_that_timed_out_and_was_not_made_fails_only_after_the_consistency_window_then_the_hold_is_released()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockBookingScenarios.TimeoutNotBookedFamilyName);
        using var checkout = await Checkout(token, order, NewKey());
        checkout.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Run(ReconcileBookingsJob.Name);
        (await LoadOrder(order)).Items[0].Status.ShouldBe(FlightOrderItemStatus.PendingConfirmation); // not found yet proves nothing

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await Run(ReconcileBookingsJob.Name);
        await Run("orders.outbox");
        await Run(ReconcilePaymentAttemptsJob.Name);

        (await LoadOrder(order)).Status.ShouldBe(OrderStatus.Failed);
        (await PaymentOf(order)).Status.ShouldBe(PaymentAttemptStatus.Voided);
    }

    [Fact]
    public async Task Checkout_needs_a_key_and_the_customers_own_order()
    {
        var token = Token();
        var order = await ReadyOrder(token);

        using var noKey = await Checkout(token, order, null);
        using var other = await Checkout(Token(), order, NewKey());

        (await Problem(noKey)).ShouldBe((HttpStatusCode.BadRequest, "idempotency-key-required"));
        (await Problem(other)).ShouldBe((HttpStatusCode.NotFound, "order-not-found"));
        (await CountPayments(order)).ShouldBe(0);
    }

    [Fact]
    public async Task Another_customer_learns_nothing_about_a_booked_order_or_a_payment_challenge()
    {
        var owner = Token();
        var booked = await ReadyOrder(owner);
        (await Checkout(owner, booked, NewKey())).Dispose();
        var challenged = await ReadyOrder(owner);
        using var challenge = await Checkout(owner, challenged, NewKey(), MockPaymentMethods.RequiresAction);
        (await Read(challenge)).GetProperty("customerAction").GetString().ShouldNotBeNullOrEmpty(); // the owner gets it

        var intruder = Token();
        foreach (var orderId in new[] { booked, challenged })
        {
            using var attempt = await Checkout(intruder, orderId, NewKey());
            var text = await attempt.Content.ReadAsStringAsync(Ct);
            attempt.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            text.ShouldNotContain("customerAction");
            text.ShouldNotContain("bookingReference");
        }
    }

    [Theory]
    [InlineData("two words")] // only visible ASCII (stored as varchar)
    [InlineData("k-01234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890")] // over 100
    public async Task A_missing_or_unusable_key_is_refused_before_anything_happens(string key)
    {
        var token = Token();
        var order = await ReadyOrder(token);

        using var response = await Checkout(token, order, key);

        (await Problem(response)).ShouldBe((HttpStatusCode.BadRequest, "idempotency-key-required"));
        (await CountPayments(order)).ShouldBe(0);
    }

    [Fact]
    public async Task A_decline_is_generic_and_the_order_can_be_paid_with_a_new_key()
    {
        var token = Token();
        var order = await ReadyOrder(token);

        using var declined = await Checkout(token, order, NewKey(), MockPaymentMethods.InsufficientFunds);
        using var paid = await Checkout(token, order, NewKey());

        var body = await Read(declined);
        (body.GetProperty("outcome").GetString(), body.GetProperty("payment").GetString()).ShouldBe(("Declined", "Declined"));
        body.ToString().ShouldNotContain("insufficient", Case.Insensitive);
        (await Read(paid)).GetProperty("outcome").GetString().ShouldBe("Booked");
    }

    // ---------- Helpers ----------

    private static string Token() => TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private static string NewKey() => $"pay-{Guid.NewGuid():N}";

    // Search, select, revalidate, order, and give one adult traveller with the contact: ready to pay.
    private async Task<Guid> ReadyOrder(string token, string surname = "Lovelace", string email = "ada@example.com")
    {
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var search = await client.PostAsJsonAsync("/api/v1/flights/searches", new { origin = "LHR", destination = "JFK", departureDate = departure }, Ct);
        var found = await Read(search);
        var searchId = found.GetProperty("searchId").GetGuid();
        var offerId = found.GetProperty("offers")[0].GetProperty("offerId").GetGuid();

        using var select = await Send(HttpMethod.Post, "/api/v1/flights/selected-offers", token, new { searchId, offerId });
        var selected = (await Read(select)).GetProperty("selectedOfferId").GetGuid();
        using var revalidated = await Send(HttpMethod.Post, $"/api/v1/flights/selected-offers/{selected}/revalidations", token);
        revalidated.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var created = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selected }, key: $"order-{Guid.NewGuid():N}");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var orderId = (await Read(created)).GetProperty("orderId").GetGuid();

        using var travellers = await Send(HttpMethod.Put, $"/api/v1/orders/{orderId}/travellers", token, new
        {
            contact = new { email, phone = "+447700900123" },
            travellers = new[] { new { type = "Adult", givenNames = "Ada", surname, dateOfBirth = "1990-12-10", gender = "Female" } },
        });
        travellers.StatusCode.ShouldBe(HttpStatusCode.OK);
        return orderId;
    }

    private Task<HttpResponseMessage> Checkout(string token, Guid orderId, string? key, string paymentMethodToken = MockPaymentMethods.Approved) =>
        Send(HttpMethod.Post, $"/api/v1/orders/{orderId}/checkout", token, new { paymentMethodToken }, key);

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

    private async Task<Notification> NoticeFor(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Notifications.AsNoTracking().SingleAsync(n => n.OrderId == orderId, Ct);
    }

    private IReadOnlyList<EmailMessage> EmailsAbout(Guid orderId) =>
        [.. api.Services.GetRequiredService<RecordingEmailSender>().Sent.Where(m => m.TextBody.Contains(orderId.ToString(), StringComparison.Ordinal))];

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
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId).OrderByDescending(a => a.CreatedAt).FirstAsync(Ct);
    }

    private async Task<int> CountPayments(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.CountAsync(a => a.OrderId == orderId, Ct);
    }

    private async Task<int> CountOutbox(Guid orderId, string eventName)
    {
        using var scope = api.Services.CreateScope();
        var id = orderId.ToString();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<OutboxMessage>()
            .CountAsync(m => m.Type.EndsWith(eventName) && m.Payload.Contains(id), Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
