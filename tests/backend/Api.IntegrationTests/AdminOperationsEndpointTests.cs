using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Customers.Domain;
using TravelBooking.Modules.Customers.Infrastructure;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Staff operations on payments and personal data (ADR 0022), over HTTP with a real SQL Server: a payment in manual
/// review is resolved only by a provider lookup (F-26), the attempt-limit review list (Q10), and the legal hold on an
/// order's personal data (Q9). Each needs its own permission, and every change is audited in the same save.
/// </summary>
public sealed class AdminOperationsEndpointTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _payments = "/api/admin/v1/payments";

    // ---------- Who may use these endpoints ----------

    [Theory]
    [InlineData("GET", "/api/admin/v1/payments/attempt-limit-reviews", TestStaffTokens.Operations)]
    [InlineData("GET", "/api/admin/v1/payments/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11", TestStaffTokens.Operations)]
    [InlineData("POST", "/api/admin/v1/payments/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/review-resolutions", TestStaffTokens.Operations)]
    [InlineData("POST", "/api/admin/v1/payments/refunds/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/review-resolutions", TestStaffTokens.Operations)]
    [InlineData("PUT", "/api/admin/v1/orders/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/legal-hold", TestStaffTokens.Privacy)]
    [InlineData("POST", "/api/admin/v1/orders/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/legal-hold/release-requests", TestStaffTokens.Privacy)]
    [InlineData("POST", "/api/admin/v1/legal-hold/release-requests/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/withdrawal", TestStaffTokens.Privacy)]
    [InlineData("POST", "/api/admin/v1/legal-hold/release-requests/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/decision", TestStaffTokens.Legal)]
    [InlineData("GET", "/api/admin/v1/legal-hold/release-requests", TestStaffTokens.Legal)]
    [InlineData("POST", "/api/admin/v1/orders/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/refund-cases", TestStaffTokens.Operations)]
    [InlineData("POST", "/api/admin/v1/refund-cases/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/withdrawal", TestStaffTokens.Operations)]
    [InlineData("POST", "/api/admin/v1/refund-cases/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11/decision", TestStaffTokens.Finance)]
    [InlineData("GET", "/api/admin/v1/refund-cases", TestStaffTokens.Finance)]
    public async Task Each_endpoint_needs_a_staff_member_with_MFA_and_its_own_permission(string method, string url, string allowed)
    {
        object? body = method switch { "POST" => new { reason = "TICKET-1" }, "PUT" => new { hold = true, reason = "CASE-1" }, _ => null };

        foreach (var token in new[] { null, TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow), TestStaffTokens.For(allowed, mfa: false) })
        {
            using var refused = await Send(new HttpMethod(method), url, token, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var other = allowed == TestStaffTokens.Privacy ? TestStaffTokens.Operations : TestStaffTokens.Privacy; // holds other permissions
        using var unassigned = await Send(new HttpMethod(method), url, TestStaffTokens.For(TestStaffTokens.Unassigned), body);
        using var wrongRole = await Send(new HttpMethod(method), url, TestStaffTokens.For(other), body);
        using var granted = await Send(new HttpMethod(method), url, TestStaffTokens.For(allowed), body);
        using var administrator = await Send(new HttpMethod(method), url, TestStaffTokens.For(TestStaffTokens.Administrator), body);

        (unassigned.StatusCode, wrongRole.StatusCode).ShouldBe((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden));
        foreach (var allowedResponse in new[] { granted, administrator })
        {
            allowedResponse.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
            allowedResponse.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        }
    }

    // ---------- Payment review (F-26) ----------

    [Fact]
    public async Task A_payment_in_review_is_resolved_to_what_the_provider_holds_audited_and_a_repeat_says_where_it_stands()
    {
        var attemptId = await PaymentInReview();

        using var resolved = await Send(HttpMethod.Post, $"{_payments}/{attemptId}/review-resolutions", Staff(TestStaffTokens.Operations), new { reason = "TICKET-55" });
        using var repeat = await Send(HttpMethod.Post, $"{_payments}/{attemptId}/review-resolutions", Staff(TestStaffTokens.Operations), new { reason = "TICKET-55" });
        using var detail = await Send(HttpMethod.Get, $"{_payments}/{attemptId}", Staff(TestStaffTokens.Operations));

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(resolved);
        (body.GetProperty("status").GetString(), body.GetProperty("resolved").GetBoolean()).ShouldBe(("Authorized", true)); // the provider holds it
        var again = (await repeat.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        (repeat.StatusCode, again.Type, again.Extensions["paymentStatus"]!.ToString()).ShouldBe((HttpStatusCode.Conflict, "not-in-review", "Authorized"));
        var history = (await Read(detail)).GetProperty("history").EnumerateArray().ToList();
        history[^1].GetProperty("actor").GetString()!.ShouldStartWith("staff:");
        var audit = (await PaymentAudit(attemptId)).ShouldHaveSingleItem();
        (audit.Action, audit.Before, audit.After).ShouldBe(("payments.review.resolve", "ManualReview", "Authorized; TICKET-55"));
    }

    [Fact]
    public async Task An_unknown_payment_and_a_reason_that_is_not_a_ticket_reference_are_refused()
    {
        var attemptId = await PaymentInReview();

        using var unknown = await Send(HttpMethod.Post, $"{_payments}/{Guid.NewGuid()}/review-resolutions", Staff(TestStaffTokens.Operations), new { reason = "TICKET-1" });
        using var badReason = await Send(HttpMethod.Post, $"{_payments}/{attemptId}/review-resolutions", Staff(TestStaffTokens.Operations), new { reason = "TICKET-1 4242 4242 4242 4242" });

        (await Problem(unknown)).ShouldBe((HttpStatusCode.NotFound, "payment-not-found"));
        badReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LoadPayment(attemptId)).Status.ShouldBe(PaymentAttemptStatus.ManualReview); // nothing happened
    }

    [Fact]
    public async Task The_attempt_limit_review_list_is_readable_by_operations()
    {
        using var response = await Send(HttpMethod.Get, $"{_payments}/attempt-limit-reviews", Staff(TestStaffTokens.Operations));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Read(response)).ValueKind.ShouldBe(JsonValueKind.Array);
    }

    // ---------- Legal hold (Q9) ----------

    [Fact]
    public async Task A_legal_hold_is_placed_in_one_step_but_released_only_when_a_different_person_approves_it()
    {
        var orderId = await OrderWithTravellers();
        var url = $"/api/admin/v1/orders/{orderId}/legal-hold";

        using var placed = await Send(HttpMethod.Put, url, Staff(TestStaffTokens.Privacy), new { hold = true, reason = "CASE-2026-17" });
        using var again = await Send(HttpMethod.Put, url, Staff(TestStaffTokens.Privacy), new { hold = true, reason = "CASE-2026-17" });
        (await Read(placed)).GetProperty("changed").GetBoolean().ShouldBeTrue();
        (await Read(again)).GetProperty("changed").GetBoolean().ShouldBeFalse();

        // ADR 0026: no direct release; a request (the hold stays), then a different person with the approve permission.
        using var direct = await Send(HttpMethod.Put, url, Staff(TestStaffTokens.Privacy), new { hold = false, reason = "CASE-2026-17 closed" });
        (await Problem(direct)).ShouldBe((HttpStatusCode.Conflict, "release-requires-approval"));
        using var requested = await Send(HttpMethod.Post, $"{url}/release-requests", Staff(TestStaffTokens.Privacy), new { reason = "CASE-2026-17 closed" });
        requested.StatusCode.ShouldBe(HttpStatusCode.Created);
        var requestId = (await Read(requested)).GetProperty("requestId").GetGuid();
        using var duplicate = await Send(HttpMethod.Post, $"{url}/release-requests", Staff(TestStaffTokens.Privacy), new { reason = "CASE-2026-17 closed" });
        (await Problem(duplicate)).ShouldBe((HttpStatusCode.Conflict, "release-pending"));
        (await TravellersHeld(orderId)).ShouldBeTrue();

        var decision = $"/api/admin/v1/legal-hold/release-requests/{requestId}/decision";
        using var byRequesterRole = await Send(HttpMethod.Post, decision, Staff(TestStaffTokens.Privacy), new { approve = true, reason = "CASE-2026-17" });
        byRequesterRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // Privacy requests; it never approves
        using var legalRequests = await Send(HttpMethod.Post, $"{url}/release-requests", Staff(TestStaffTokens.Legal), new { reason = "CASE-2026-17" });
        legalRequests.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // Legal approves; it never requests
        using var approved = await Send(HttpMethod.Post, decision, Staff(TestStaffTokens.Legal), new { approve = true, reason = "CASE-2026-17 checked" });
        (await Read(approved)).GetProperty("status").GetString().ShouldBe("Approved");
        (await TravellersHeld(orderId)).ShouldBeFalse();

        using var unassignedStatus = await Send(HttpMethod.Get, url, Staff(TestStaffTokens.Unassigned));
        unassignedStatus.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // orders.read only
        using var status = await Send(HttpMethod.Get, url, Staff(TestStaffTokens.Operations));
        var state = await Read(status);
        state.GetProperty("held").GetBoolean().ShouldBeFalse();
        DateOnly.Parse(state.GetProperty("purgeNotBefore").GetString()!, CultureInfo.InvariantCulture)
            .ShouldBe(DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30)); // the grace period before any purge

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var target = $"order:{orderId}";
        (await db.Set<AuditEntry>().AsNoTracking().Where(a => a.Target == target).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["personal-data.legal-hold", "personal-data.legal-hold.release-request", "personal-data.legal-hold.release-approve"], ignoreOrder: true);
        (await db.Set<RetentionEvent>().AsNoTracking().Where(e => e.OrderId == orderId).Select(e => e.Actor).ToListAsync(Ct))
            .ShouldAllBe(actor => actor.StartsWith("staff:"));
    }

    [Fact]
    public async Task Nobody_approves_their_own_release_and_only_the_requester_withdraws_it()
    {
        var orderId = await OrderWithTravellers();
        var url = $"/api/admin/v1/orders/{orderId}/legal-hold";
        (await Send(HttpMethod.Put, url, Staff(TestStaffTokens.Privacy), new { hold = true, reason = "CASE-31" })).Dispose();
        using var requested = await Send(HttpMethod.Post, $"{url}/release-requests", Staff(TestStaffTokens.Administrator), new { reason = "CASE-31 closed" });
        var requestId = (await Read(requested)).GetProperty("requestId").GetGuid();
        var release = $"/api/admin/v1/legal-hold/release-requests/{requestId}";

        using var selfApproval = await Send(HttpMethod.Post, $"{release}/decision", Staff(TestStaffTokens.Administrator), new { approve = true, reason = "CASE-31" });
        using var othersWithdrawal = await Send(HttpMethod.Post, $"{release}/withdrawal", Staff(TestStaffTokens.Privacy), new { reason = "CASE-31" });
        (await Problem(selfApproval)).ShouldBe((HttpStatusCode.Forbidden, "self-approval-not-allowed"));
        (await Problem(othersWithdrawal)).ShouldBe((HttpStatusCode.Forbidden, "withdrawal-requester-only"));
        using var pending = await Send(HttpMethod.Get, "/api/admin/v1/legal-hold/release-requests", Staff(TestStaffTokens.SecondAdministrator));
        (await Read(pending)).EnumerateArray().ShouldContain(r => r.GetProperty("requestId").GetGuid() == requestId);

        using var withdrawn = await Send(HttpMethod.Post, $"{release}/withdrawal", Staff(TestStaffTokens.Administrator), new { reason = "CASE-31 not needed" });
        (await Read(withdrawn)).GetProperty("status").GetString().ShouldBe("Rejected");
        (await TravellersHeld(orderId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_legal_hold_on_an_order_without_personal_data_or_without_a_case_reference_is_refused()
    {
        var orderId = await OrderWithTravellers();

        using var unknown = await Send(HttpMethod.Put, $"/api/admin/v1/orders/{Guid.NewGuid()}/legal-hold", Staff(TestStaffTokens.Privacy), new { hold = true, reason = "CASE-1" });
        using var noReason = await Send(HttpMethod.Put, $"/api/admin/v1/orders/{orderId}/legal-hold", Staff(TestStaffTokens.Privacy), new { hold = true, reason = "Ada Lovelace's case!" });

        (await Problem(unknown)).ShouldBe((HttpStatusCode.NotFound, "personal-data-not-found"));
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TravellersHeld(orderId)).ShouldBeFalse();
    }

    // ---------- Helpers ----------

    private static string Staff(string objectId) => TestStaffTokens.For(objectId);

    // An authorization whose answer was lost (the provider holds it), then put in manual review.
    private async Task<Guid> PaymentInReview()
    {
        using var scope = api.Services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<IOrderPayments>();
        var result = await payments.AuthorizeAsync(
            new OrderPaymentRequest(Guid.NewGuid(), "admin-tests", $"pay-{Guid.NewGuid():N}", new Money(540m, new CurrencyCode("XTS")), MockPaymentMethods.TimeoutAuthorized, "trace"), Ct);
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var attempt = await db.PaymentAttempts.Include(a => a.Events).SingleAsync(a => a.Id == result.Value.PaymentId, Ct);
        attempt.Resolve(PaymentAttemptStatus.ManualReview, "an unexpected answer (test)", new PaymentChange(api.Clock.GetUtcNow(), "system:test", null)).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
        return attempt.Id;
    }

    private async Task<Guid> OrderWithTravellers()
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
        using var travellers = await Send(HttpMethod.Put, $"/api/v1/orders/{orderId}/travellers", token, new
        {
            contact = new { email = "ada@example.com", phone = "+447700900123" },
            travellers = new[] { new { type = "Adult", givenNames = "Ada", surname = "Lovelace", dateOfBirth = "1990-12-10", gender = "Female" } },
        });
        travellers.StatusCode.ShouldBe(HttpStatusCode.OK);
        return orderId;
    }

    private async Task<bool> TravellersHeld(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Set<OrderTravellerSet>().AsNoTracking()
            .Where(s => s.OrderId == orderId).Select(s => s.LegalHold).SingleAsync(Ct);
    }

    private async Task<PaymentAttempt> LoadPayment(Guid attemptId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct);
    }

    private async Task<List<AuditEntry>> PaymentAudit(Guid attemptId)
    {
        using var scope = api.Services.CreateScope();
        var target = $"payment-attempt:{attemptId}";
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<AuditEntry>().AsNoTracking().Where(a => a.Target == target).ToListAsync(Ct);
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

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
