using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Staff endpoints for orders (ADR 0008, ADR 0022), over HTTP with a real SQL Server: a separate route group that only a
/// staff token with MFA and the named permission may use (never a customer token), read-only queues and timelines, and
/// the way out of a booking's manual review by a supplier lookup, settled and audited in the same save (F-13).
/// </summary>
public sealed class AdminOrderEndpointTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _orders = "/api/admin/v1/orders";

    // ---------- Who may use the admin API ----------

    // The permission matrix, for every admin endpoint (testing rules): anonymous, a customer, staff without MFA, a token
    // carrying our reserved claims, an app-only token or one without an account are refused (401); a staff member
    // without the permission is forbidden (403).
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11")]
    [InlineData("POST", "/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/items/0b6a4d1e-2c3b-4a55-9f86-2f6d3a0b1c22/review-checks")]
    [InlineData("POST", "/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/items/0b6a4d1e-2c3b-4a55-9f86-2f6d3a0b1c22/review-outcomes")]
    public async Task Only_a_staff_member_with_MFA_and_the_permission_may_use_each_admin_endpoint(string method, string path)
    {
        var url = _orders + path;
        var body = method == "POST" ? new { reason = "TICKET-1" } : null;
        string?[] refused =
        [
            null,
            TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow),
            TestStaffTokens.For(TestStaffTokens.Operations, mfa: false),
            TestStaffTokens.For(TestStaffTokens.Operations, extraClaims: [new Claim("TB_PERMISSION", "orders.read")]),
            TestStaffTokens.For(TestStaffTokens.Operations, extraClaims: [new Claim("idtyp", "app")]),
            TestStaffTokens.For(objectId: null),
        ];

        foreach (var token in refused)
        {
            using var response = await Send(new HttpMethod(method), url, token, body);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var unassigned = await Send(new HttpMethod(method), url, TestStaffTokens.For(TestStaffTokens.Unassigned), body);
        using var operations = await Send(new HttpMethod(method), url, Staff(), body);
        unassigned.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // signed in, but no role grants the permission
        operations.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        operations.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task With_a_tenant_scope_required_a_staff_token_without_it_is_forbidden()
    {
        using var scoped = api.WithWebHostBuilder(b => b.UseSetting("Authentication:Staff:RequiredScope", "staff.operations"));
        using var client = scoped.CreateClient();

        using var without = await SendWith(client, TestStaffTokens.For(TestStaffTokens.Operations));
        using var with = await SendWith(client, TestStaffTokens.For(TestStaffTokens.Operations, extraClaims: [new Claim("scp", "staff.operations")]));

        (without.StatusCode, with.StatusCode).ShouldBe((HttpStatusCode.Forbidden, HttpStatusCode.OK));

        static async Task<HttpResponseMessage> SendWith(HttpClient client, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _orders);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, Ct);
        }
    }

    [Theory]
    [InlineData("staff-operations", "Operations", "staff-operations", "Administrator")] // one account twice
    [InlineData("staff-x", "SuperUser", null, null)] // an unknown role
    public void Invalid_role_assignments_stop_the_host_at_startup(string firstId, string firstRole, string? secondId, string? secondRole)
    {
        using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Access:RoleAssignments:0:ObjectId", firstId);
            b.UseSetting("Access:RoleAssignments:0:Roles:0", firstRole);
            if (secondId is not null)
            {
                b.UseSetting("Access:RoleAssignments:1:ObjectId", secondId);
                b.UseSetting("Access:RoleAssignments:1:Roles:0", secondRole);
            }
        });

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public async Task A_staff_token_never_works_on_customer_endpoints()
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/customers/me", Staff());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------- Queues and timelines ----------

    [Fact]
    public async Task The_review_queue_and_an_orders_timeline_show_operations_data_but_no_personal_data()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName);

        var queue = await ReadAll(ManualReviewQueue());
        using var detail = await Send(HttpMethod.Get, $"{_orders}/{orderId}", Staff());
        var body = await detail.Content.ReadAsStringAsync(Ct);

        queue.ShouldContain(orderId);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var timeline = JsonDocument.Parse(body).RootElement.GetProperty("timeline");
        timeline.EnumerateArray().Select(e => e.GetProperty("toStatus").GetString()).ShouldContain("ManualReview");
        body.ShouldNotContain("Ada", Case.Sensitive);
        body.ShouldNotContain("ada@example.com");
        (await Send(HttpMethod.Get, $"{_orders}/{Guid.NewGuid()}", Staff())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Paging_through_the_queue_never_skips_orders_created_at_the_same_instant()
    {
        var first = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName);
        var second = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName); // the clock did not move: same CreatedAt

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            using var page = await Send(HttpMethod.Get, $"{_orders}?itemStatus=ManualReview&limit=1" + (cursor is null ? string.Empty : $"&cursor={cursor}"), Staff());
            var body = await Read(page);
            seen.AddRange(body.GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("orderId").GetGuid()));
            cursor = body.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        seen.ShouldContain(first);
        seen.ShouldContain(second);
        seen.ShouldBeUnique();
    }

    [Fact]
    public async Task The_staff_api_has_its_own_document_and_never_appears_in_the_customer_contract()
    {
        using var client = api.CreateClient();

        var customer = await client.GetStringAsync("/openapi/v1.json", Ct);
        var staff = await client.GetStringAsync("/openapi/admin-v1.json", Ct);

        customer.ShouldNotContain("/api/admin");
        staff.ShouldContain("/api/admin/v1/orders/{orderId}/items/{itemId}/review-checks");
    }

    [Theory]
    [InlineData("?itemStatus=Confirmed")] // not an operations queue
    [InlineData("?limit=51")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?cursor=123")] // the time without the order id
    public async Task An_invalid_queue_query_is_a_client_error(string query)
    {
        using var response = await Send(HttpMethod.Get, _orders + query, Staff());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------- Manual review: settled by a supplier lookup only ----------

    [Fact]
    public async Task A_booking_in_review_found_at_the_supplier_is_confirmed_charged_and_audited()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName);
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        using var check = await Check(orderId, itemId, "TICKET-123");

        check.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await Read(check);
        (result.GetProperty("itemStatus").GetString(), result.GetProperty("resolved").GetBoolean()).ShouldBe(("Confirmed", true));
        var order = await LoadOrder(orderId);
        order.Timeline[^2].Actor.ShouldStartWith("staff:"); // the confirmation, by the staff member
        (await CountOutbox(orderId, "OrderPaymentCaptureRequested")).ShouldBe(1);
        var audit = (await AuditEntries(itemId)).ShouldHaveSingleItem();
        (audit.Action, audit.Before, audit.After).ShouldBe(("bookings.review.check", "ManualReview", "Confirmed; TICKET-123"));
        audit.Actor.ShouldBe(order.Timeline[^2].Actor);
    }

    [Fact]
    public async Task Not_found_inside_the_window_settles_nothing_and_after_it_fails_the_booking_and_releases_the_hold()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutNotBookedFamilyName);
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        using var early = await Check(orderId, itemId, "TICKET-200");
        (await Read(early)).GetProperty("resolved").GetBoolean().ShouldBeFalse();
        (await LoadOrder(orderId)).Items[0].Status.ShouldBe(OrderItemStatus.ManualReview);

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        using var later = await Check(orderId, itemId, "TICKET-200");

        (await Read(later)).GetProperty("itemStatus").GetString().ShouldBe("Failed");
        (await CountOutbox(orderId, "OrderPaymentReleaseRequested")).ShouldBe(1);
        (await CountOutbox(orderId, "OrderPaymentCaptureRequested")).ShouldBe(0);
        (await AuditEntries(itemId)).Count.ShouldBe(2);
    }

    // ---------- Manual review: a person's outcome (ADR 0025) ----------

    [Fact]
    public async Task A_booking_cancelled_at_the_supplier_charges_nothing_releases_the_hold_and_tells_the_customer()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName, seenBooking: "mock:WRONG1");
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        using var noEvidence = await Outcome(orderId, itemId, new { outcome = "CancelledAtSupplier", reason = "TICKET-401" });
        noEvidence.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // the supplier desk's reference is the evidence
        using var cancelled = await Outcome(orderId, itemId, new { outcome = "CancelledAtSupplier", supplierReference = "DESK-CXL-77", reason = "TICKET-401" });

        (await Read(cancelled)).GetProperty("itemStatus").GetString().ShouldBe("Failed");
        var order = await LoadOrder(orderId);
        order.Timeline[^2].ProviderReference.ShouldBe("DESK-CXL-77");
        (await CountOutbox(orderId, "OrderPaymentReleaseRequested")).ShouldBe(1);
        (await CountOutbox(orderId, "OrderPaymentCaptureRequested")).ShouldBe(0);
        (await CountOutbox(orderId, "OrderBookingSettled")).ShouldBe(1);
        var audit = (await AuditEntries(itemId)).ShouldHaveSingleItem();
        (audit.Action, audit.After).ShouldBe(("bookings.review.outcome", "Failed (CancelledAtSupplier; seen booking mock:WRONG1; supplier reference DESK-CXL-77); TICKET-401"));
        using var again = await Outcome(orderId, itemId, new { outcome = "CancelledAtSupplier", supplierReference = "DESK-CXL-77", reason = "TICKET-401" });
        (await Problem(again)).ShouldBe((HttpStatusCode.Conflict, "not-in-review"));
    }

    [Fact]
    public async Task A_booking_seen_not_as_agreed_is_accepted_only_with_both_confirmations_and_charges_the_agreed_price()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName, seenBooking: "mock:SEEN42");
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        using var unconfirmed = await Outcome(orderId, itemId, new { outcome = "AcceptAsBooked", sameTravellersAndFlights = true, reason = "TICKET-402" });
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var accepted = await Outcome(orderId, itemId,
            new { outcome = "AcceptAsBooked", sameTravellersAndFlights = true, priceNotAboveAgreed = true, reason = "TICKET-402" });

        (await Read(accepted)).GetProperty("itemStatus").GetString().ShouldBe("Confirmed");
        var order = await LoadOrder(orderId);
        (order.Items[0].ProviderId, order.Items[0].SupplierLocator).ShouldBe(("mock", "SEEN42"));
        (await CountOutbox(orderId, "OrderPaymentCaptureRequested")).ShouldBe(1); // the agreed price, never more
    }

    [Fact]
    public async Task Nothing_is_accepted_when_no_supplier_booking_was_ever_seen()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName); // unknown, not a mismatch
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        using var accepted = await Outcome(orderId, itemId,
            new { outcome = "AcceptAsBooked", sameTravellersAndFlights = true, priceNotAboveAgreed = true, reason = "TICKET-403" });
        using var withoutPermission = await Send(HttpMethod.Post, $"{_orders}/{orderId}/items/{itemId}/review-outcomes", TestStaffTokens.For(TestStaffTokens.Privacy),
            new { outcome = "CancelledAtSupplier", supplierReference = "DESK-1", reason = "TICKET-403" });

        using var cancelled = await Outcome(orderId, itemId, new { outcome = "CancelledAtSupplier", supplierReference = "DESK-2", reason = "TICKET-403" });

        (await Problem(accepted)).ShouldBe((HttpStatusCode.Conflict, "no-supplier-booking-seen"));
        (await Problem(cancelled)).ShouldBe((HttpStatusCode.Conflict, "no-supplier-booking-seen")); // only a lookup releases this hold
        (await CountOutbox(orderId, "OrderPaymentReleaseRequested")).ShouldBe(0);
        withoutPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoadOrder(orderId)).Items[0].Status.ShouldBe(OrderItemStatus.ManualReview);
    }

    // Concurrency (testing rules): outcomes recorded at the same moment settle the payment exactly once.
    [Fact]
    public async Task Parallel_outcomes_charge_once_and_notify_once()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName, seenBooking: "mock:PAR77");
        var itemId = (await LoadOrder(orderId)).Items[0].Id;
        var accept = new { outcome = "AcceptAsBooked", sameTravellersAndFlights = true, priceNotAboveAgreed = true, reason = "TICKET-404" };

        var responses = await Task.WhenAll(
            Outcome(orderId, itemId, accept), Outcome(orderId, itemId, accept),
            Outcome(orderId, itemId, new { outcome = "CancelledAtSupplier", supplierReference = "DESK-404", reason = "TICKET-404" }),
            Check(orderId, itemId, "TICKET-404"));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        var settled = (await CountOutbox(orderId, "OrderPaymentCaptureRequested")) + (await CountOutbox(orderId, "OrderPaymentReleaseRequested"));
        settled.ShouldBe(1);
        (await CountOutbox(orderId, "OrderBookingSettled")).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private Task<HttpResponseMessage> Outcome(Guid orderId, Guid itemId, object body) =>
        Send(HttpMethod.Post, $"{_orders}/{orderId}/items/{itemId}/review-outcomes", Staff(), body);

    [Fact]
    public async Task Only_items_in_review_can_be_checked_and_a_reason_is_required()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName);
        var itemId = (await LoadOrder(orderId)).Items[0].Id;
        (await Check(orderId, itemId, "TICKET-1")).Dispose(); // confirmed now

        using var again = await Check(orderId, itemId, "TICKET-1");
        using var unknown = await Check(orderId, Guid.NewGuid(), "TICKET-1");
        using var noReason = await Check(orderId, itemId, "x");
        using var notAReference = await Check(orderId, itemId, "pax <b>Ada</b> dob 1990"); // only a ticket reference or a plain note
        using var withoutPermission = await Send(HttpMethod.Post, $"{_orders}/{orderId}/items/{itemId}/review-checks", TestStaffTokens.For(TestStaffTokens.Unassigned), new { reason = "TICKET-1" });

        var repeat = (await again.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        (again.StatusCode, repeat.Type).ShouldBe((HttpStatusCode.Conflict, "not-in-review"));
        repeat.Extensions["itemStatus"]!.ToString().ShouldBe("Confirmed"); // where it stands
        (await Problem(unknown)).ShouldBe((HttpStatusCode.NotFound, "order-item-not-found"));
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        notAReference.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        withoutPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parallel_checks_of_one_booking_confirm_it_once_and_charge_once()
    {
        var orderId = await OrderInReview(MockBookingScenarios.TimeoutBookedFamilyName);
        var itemId = (await LoadOrder(orderId)).Items[0].Id;

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Check(orderId, itemId, "TICKET-9")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await LoadOrder(orderId)).Timeline.Count(e => e is { FromStatus: "ManualReview", ToStatus: "Confirmed" }).ShouldBe(1);
        (await CountOutbox(orderId, "OrderPaymentCaptureRequested")).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // ---------- Helpers ----------

    private static string Staff() => TestStaffTokens.For(TestStaffTokens.Operations);

    private Task<HttpResponseMessage> Check(Guid orderId, Guid itemId, string reason) =>
        Send(HttpMethod.Post, $"{_orders}/{orderId}/items/{itemId}/review-checks", Staff(), new { reason });

    private async Task<HttpResponseMessage> ManualReviewQueue() => await Send(HttpMethod.Get, $"{_orders}?itemStatus=ManualReview&limit=50", Staff());

    private static async Task<List<Guid>> ReadAll(Task<HttpResponseMessage> response)
    {
        using var page = await response;
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await Read(page)).GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("orderId").GetGuid())];
    }

    // A booked checkout whose outcome was unknown, then put in manual review (as reconciliation does after its limit).
    // seenBooking: the "provider:locator" of a supplier booking seen not as agreed (a mismatch), as booking records it.
    private async Task<Guid> OrderInReview(string surname, string? seenBooking = null)
    {
        var token = TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var search = await client.PostAsJsonAsync("/api/v1/flights/searches", new { origin = "LHR", destination = "JFK", departureDate = departure }, Ct);
        var found = await Read(search);
        using var select = await Send(HttpMethod.Post, "/api/v1/flights/selected-offers", token,
            new { searchId = found.GetProperty("searchId").GetGuid(), offerId = found.GetProperty("offers")[0].GetProperty("offerId").GetGuid() });
        var selected = (await Read(select)).GetProperty("selectedOfferId").GetGuid();
        (await Send(HttpMethod.Post, $"/api/v1/flights/selected-offers/{selected}/revalidations", token)).Dispose();
        using var created = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selected }, $"order-{Guid.NewGuid():N}");
        var orderId = (await Read(created)).GetProperty("orderId").GetGuid();
        (await Send(HttpMethod.Put, $"/api/v1/orders/{orderId}/travellers", token, new
        {
            contact = new { email = "ada@example.com", phone = "+447700900123" },
            travellers = new[] { new { type = "Adult", givenNames = "Ada", surname, dateOfBirth = "1990-12-10", gender = "Female" } },
        })).Dispose();
        using var checkout = await Send(HttpMethod.Post, $"/api/v1/orders/{orderId}/checkout", token, new { paymentMethodToken = MockPaymentMethods.Approved }, $"pay-{Guid.NewGuid():N}");
        checkout.StatusCode.ShouldBe(HttpStatusCode.Accepted); // pending: the supplier's answer was lost

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Timeline).SingleAsync(o => o.Id == orderId, Ct);
        order.RequireManualReview(order.Items[0].Id, seenBooking is null ? "Booking still unknown after its limit (test)" : "Not as agreed (test)",
            new TransitionContext(api.Clock.GetUtcNow(), "system:test"), seenBooking).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null, string? key = null)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<Order> LoadOrder(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders.AsNoTracking()
            .Include(o => o.Items).Include(o => o.Timeline).AsSplitQuery().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task<int> CountOutbox(Guid orderId, string eventName)
    {
        using var scope = api.Services.CreateScope();
        var id = orderId.ToString();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<OutboxMessage>()
            .CountAsync(m => m.Type.EndsWith(eventName) && m.Payload.Contains(id), Ct);
    }

    private async Task<List<AuditEntry>> AuditEntries(Guid itemId)
    {
        using var scope = api.Services.CreateScope();
        var target = $"order-item:{itemId}";
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Set<AuditEntry>().AsNoTracking()
            .Where(a => a.Target == target).OrderBy(a => a.Id).ToListAsync(Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
