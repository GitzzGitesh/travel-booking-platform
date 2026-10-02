using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Orders.Application;
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
    private async Task<Guid> ReadyOrder(string token, string surname = "Lovelace")
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
            contact = new { email = "ada@example.com", phone = "+447700900123" },
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
