extern alias worker;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Infrastructure;
using worker::TravelBooking.Worker;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The Api and the Worker as two hosts (ADR 0007), sharing only the database, with the mock payment provider's state
/// shared in SQL (ADR 0032): the Worker captures, voids and refunds what the Api's checkout authorized. Before ADR 0032
/// each host's mock kept its own payments, and the Worker's capture was refused.
/// </summary>
public sealed class CrossProcessPaymentTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>, IDisposable
{
    private readonly IHost _worker = WorkerHost(api);

    [Fact]
    public async Task The_worker_captures_what_the_api_authorized_then_pays_an_approved_refund()
    {
        var token = Token();
        var order = await ReadyOrder(token);
        using var checkout = await Checkout(token, order);
        (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("Booked");

        await RunInWorker("orders.outbox");
        await RunInWorker(ReconcilePaymentAttemptsJob.Name);

        var payment = await PaymentOf(order);
        payment.Status.ShouldBe(PaymentAttemptStatus.Captured);

        // A goodwill refund opened and approved by staff in the Api, paid by the Worker.
        using var opened = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", TestStaffTokens.For(TestStaffTokens.Operations),
            new { kind = "Goodwill", amount = "10", reason = "TICKET-700" }, Key());
        var caseId = (await Read(opened)).GetProperty("caseId").GetGuid();
        using var approved = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", TestStaffTokens.For(TestStaffTokens.Finance),
            new { approve = true, reason = "TICKET-700 checked" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunInWorker("orders.outbox");
        await RunInWorker(ExecuteRefundsJob.Name);

        using var scope = api.Services.CreateScope();
        var refund = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<RefundRecord>().AsNoTracking().SingleAsync(r => r.Id == caseId, Ct);
        refund.Status.ShouldBe(RefundRecordStatus.Succeeded);
    }

    // The customer cancels in the Api, Operations record the supplier's cancellation (desk, R8) and Finance approves it if
    // asked, in the Api; the Worker pays the refund on the payment the Worker captured.
    [Fact]
    public async Task The_worker_refunds_a_cancellation_the_customer_requested_in_the_api()
    {
        var token = Token();
        var order = await ReadyOrder(token);
        using (var checkout = await Checkout(token, order))
        {
            (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("Booked");
        }

        await RunInWorker("orders.outbox");
        await RunInWorker(ReconcilePaymentAttemptsJob.Name);
        var payment = await PaymentOf(order);
        payment.Status.ShouldBe(PaymentAttemptStatus.Captured);

        using var requested = await Send(HttpMethod.Post, $"/api/v1/orders/{order}/cancellation-requests", token, key: Key());
        requested.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var mine = await Send(HttpMethod.Get, $"/api/v1/orders/{order}", token);
        var item = (await Read(mine)).GetProperty("items")[0];
        using var recorded = await Send(HttpMethod.Post, $"/api/admin/v1/orders/{order}/refund-cases", TestStaffTokens.For(TestStaffTokens.Operations), new
        {
            kind = "Cancellation",
            itemIds = new[] { item.GetProperty("itemId").GetGuid() },
            supplierReference = "DESK-CXL-700",
            supplierRefund = item.GetProperty("agreedPrice").GetProperty("amount").GetString(),
            reason = "TICKET-701",
        }, Key());
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created);
        var recordedCase = await Read(recorded);
        var caseId = recordedCase.GetProperty("caseId").GetGuid();
        if (recordedCase.GetProperty("status").GetString() == "PendingApproval")
        {
            using var approved = await Send(HttpMethod.Post, $"/api/admin/v1/refund-cases/{caseId}/decision", TestStaffTokens.For(TestStaffTokens.Finance),
                new { approve = true, reason = "TICKET-701 checked" });
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await RunInWorker("orders.outbox");
        await RunInWorker(ExecuteRefundsJob.Name);

        using var scope = api.Services.CreateScope();
        var refund = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<RefundRecord>().AsNoTracking().SingleAsync(r => r.Id == caseId, Ct);
        refund.Status.ShouldBe(RefundRecordStatus.Succeeded);
    }

    [Fact]
    public async Task The_worker_releases_the_hold_the_api_made_for_a_booking_the_supplier_refused()
    {
        var token = Token();
        var order = await ReadyOrder(token, MockBookingScenarios.RejectedFamilyName);
        using var checkout = await Checkout(token, order);
        (await Read(checkout)).GetProperty("outcome").GetString().ShouldBe("BookingFailed");

        await RunInWorker("orders.outbox");
        await RunInWorker(ReconcilePaymentAttemptsJob.Name);

        var payment = await PaymentOf(order);
        (payment.Status, payment.CaptureRequestedAt).ShouldBe((PaymentAttemptStatus.Voided, (DateTimeOffset?)null));
    }

    public void Dispose() => _worker.Dispose();

    // The Worker exactly as it is composed (WorkerComposition), in Development, on the Api's database and clock.
    private static IHost WorkerHost(SqlApiFactory api)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flights"] = api.ConnectionString,
            ["ConnectionStrings:Hotels"] = api.ConnectionString,
            ["ConnectionStrings:Orders"] = api.ConnectionString,
            ["ConnectionStrings:Payments"] = api.ConnectionString,
            ["ConnectionStrings:Customers"] = api.ConnectionString,
            ["ConnectionStrings:Notifications"] = api.ConnectionString,
            // A document key made for this run only (ADR 0020); the jobs run here never read a document.
            ["Customers:DocumentEncryption:ActiveKeyId"] = "test-1",
            ["Customers:DocumentEncryption:Keys:test-1"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        });
        builder.AddWorkerServices();
        builder.Services.AddSingleton<TimeProvider>(api.Clock);
        return builder.Build();
    }

    private async Task RunInWorker(string job)
    {
        var schedule = _worker.Services.GetServices<BackgroundJobSchedule>().Single(s => s.Name == job);
        using var scope = _worker.Services.CreateScope();
        await ((IBackgroundJob)scope.ServiceProvider.GetRequiredService(schedule.JobType)).RunOnceAsync(Ct);
    }

    private static string Token() => TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private static string Key() => $"pay-{Guid.NewGuid():N}";

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

        using var created = await Send(HttpMethod.Post, "/api/v1/orders", token, new { selectedOfferId = selected }, $"order-{Guid.NewGuid():N}");
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

    private Task<HttpResponseMessage> Checkout(string token, Guid orderId) =>
        Send(HttpMethod.Post, $"/api/v1/orders/{orderId}/checkout", token, new { paymentMethodToken = MockPaymentMethods.Approved }, Key());

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

    private async Task<PaymentAttempt> PaymentOf(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId).OrderByDescending(a => a.CreatedAt).FirstAsync(Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
