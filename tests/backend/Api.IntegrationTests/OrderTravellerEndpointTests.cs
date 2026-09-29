using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Customers.Domain;
using TravelBooking.Modules.Customers.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The travellers and booker contact of a customer's order over HTTP, with a real SQL Server (Q9, ADR 0020): only the
/// owner can give or see them, only for the order's passengers, documents only when the supplier requires them and
/// only as ciphertext, and the retention job shreds and anonymises on time unless a legal hold applies. The per-customer
/// rate limit (Q10) covers these endpoints and the orders.
/// </summary>
public sealed class OrderTravellerEndpointTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task The_owner_gives_one_traveller_per_passenger_and_reads_them_back_without_any_document_data()
    {
        var token = Token();
        var order = await NewOrder("JFK", token);
        order.Needs.ShouldBe(new Needs(1, 0, 0, false));

        using var saved = await PutTravellers(token, order.Id, Adult());
        using var read = await Send(HttpMethod.Get, Travellers(order.Id), token);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await read.Content.ReadAsStringAsync(Ct);
        var traveller = JsonDocument.Parse(body).RootElement.GetProperty("travellers")[0];
        traveller.GetProperty("givenNames").GetString().ShouldBe("Ada");
        traveller.GetProperty("documentProvided").GetBoolean().ShouldBeFalse();
        (await Readiness(order.Id, token)).ShouldBe(new OrderTravellersReadiness(1, 0, 0, ContactProvided: true, DocumentsProvided: 0));
    }

    [Fact]
    public async Task Another_customer_can_neither_see_nor_change_the_travellers_and_learns_nothing()
    {
        var owner = Token();
        var other = Token();
        var order = await NewOrder("JFK", owner);
        (await PutTravellers(owner, order.Id, Adult())).Dispose();

        using var read = await Send(HttpMethod.Get, Travellers(order.Id), other);
        using var write = await PutTravellers(other, order.Id, Adult("Mallory"));
        using var document = await PutDocument(other, order.Id, 0);
        using var anonymous = await PutTravellers(null, order.Id, Adult());

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(write)).ShouldBe((HttpStatusCode.NotFound, "order-not-found"));
        (await Problem(document)).ShouldBe((HttpStatusCode.NotFound, "order-not-found"));
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Read(await Send(HttpMethod.Get, Travellers(order.Id), owner))).GetProperty("travellers")[0].GetProperty("givenNames").GetString().ShouldBe("Ada");
    }

    [Fact]
    public async Task Travellers_must_match_the_passengers_and_be_valid()
    {
        var token = Token();
        var order = await NewOrder("JFK", token);

        using var two = await PutTravellers(token, order.Id, Adult(), Adult("Grace"));
        using var child = await PutTravellers(token, order.Id, Adult() with { Type = "Child" });
        using var phone = await PutTravellers(token, order.Id, [Adult()], phone: "07700900123");
        using var malformed = await PutTravellers(token, order.Id, Adult() with { Gender = "Unknown" });

        (await Problem(two)).ShouldBe((HttpStatusCode.UnprocessableEntity, "travellers-do-not-match"));
        (await Problem(child)).ShouldBe((HttpStatusCode.UnprocessableEntity, "travellers-do-not-match"));
        (await Problem(phone)).ShouldBe((HttpStatusCode.UnprocessableEntity, "invalid-traveller-details"));
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // only the values the supplier supports
    }

    [Fact]
    public async Task No_document_is_accepted_when_the_supplier_does_not_require_one()
    {
        var token = Token();
        var order = await NewOrder("JFK", token);
        (await PutTravellers(token, order.Id, Adult())).Dispose();

        using var document = await PutDocument(token, order.Id, 0);

        (await Problem(document)).ShouldBe((HttpStatusCode.Conflict, "documents-not-required"));
        (await CountDocuments(order.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task When_the_supplier_requires_documents_they_are_stored_encrypted_audited_and_never_returned()
    {
        var token = Token();
        var order = await NewOrder(_documentsRequiredDestination, token);
        order.Needs.ShouldBe(new Needs(1, 0, 0, true));
        (await PutTravellers(token, order.Id, Adult())).Dispose();

        using var noTraveller = await PutDocument(token, order.Id, 3);
        using var stored = await PutDocument(token, order.Id, 0);

        (await Problem(noTraveller)).ShouldBe((HttpStatusCode.NotFound, "traveller-not-found"));
        stored.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var body = await (await Send(HttpMethod.Get, Travellers(order.Id), token)).Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("P1234567");
        JsonDocument.Parse(body).RootElement.GetProperty("travellers")[0].GetProperty("documentProvided").GetBoolean().ShouldBeTrue();
        (await Readiness(order.Id, token)).DocumentsProvided.ShouldBe(1);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var row = await db.Set<TravelDocument>().AsNoTracking().SingleAsync(d => d.OrderId == order.Id, Ct);
        Encoding.UTF8.GetString(row.Ciphertext!).ShouldNotContain("P1234567");
        (await db.Set<DocumentAccess>().AsNoTracking().Where(a => a.DocumentId == row.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe([DocumentAccessAction.Stored]);
    }

    [Fact]
    public async Task Documents_are_shredded_30_days_after_travel_personal_data_anonymised_after_25_months_unless_held()
    {
        var token = Token();
        var held = await NewOrder(_documentsRequiredDestination, token);
        var released = await NewOrder(_documentsRequiredDestination, token);
        foreach (var order in new[] { held, released })
        {
            (await PutTravellers(token, order.Id, Adult())).Dispose();
            (await PutDocument(token, order.Id, 0)).Dispose();
        }

        (await Hold(held.Id, true)).ShouldBe(LegalHoldOutcome.Applied);
        api.Clock.Advance(TimeSpan.FromDays(30 + 32)); // past the last flight (30 days out) + 30 days
        await Purge();

        (await CountDocuments(released.Id)).ShouldBe(0);
        (await CountDocuments(held.Id)).ShouldBe(1);
        (await Send(HttpMethod.Get, Travellers(released.Id), token)).StatusCode.ShouldBe(HttpStatusCode.OK); // still kept

        api.Clock.Advance(TimeSpan.FromDays(31 * 25));
        await Purge();

        (await Send(HttpMethod.Get, Travellers(released.Id), token)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // anonymised
        (await Send(HttpMethod.Get, Travellers(held.Id), token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var scope = api.Services.CreateScope())
        {
            var set = await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Set<OrderTravellerSet>().AsNoTracking()
                .Include(s => s.Travellers).SingleAsync(s => s.OrderId == released.Id, Ct);
            (set.ContactEmail, set.ContactPhone, set.Travellers[0].Surname).ShouldBe((null, null, null));
            set.Travellers[0].Kind.ShouldBe(PassengerKind.Adult);
        }

        (await Hold(held.Id, false)).ShouldBe(LegalHoldOutcome.Applied);
        await Purge();
        (await CountDocuments(held.Id)).ShouldBe(0);
        (await Send(HttpMethod.Get, Travellers(held.Id), token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Each_customer_has_their_own_limit_on_writes_while_reads_are_limited_by_address_only()
    {
        using var limited = Limited(new() { ["RateLimiting:Customer:PermitLimit"] = "2" });
        var busy = Token();
        var other = Token();
        var url = Travellers(Guid.NewGuid());

        var writes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await PutTravellers(limited, busy, url);
            writes.Add(response.StatusCode);
        }

        using var order = await SendAsync(limited, new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(new { selectedOfferId = Guid.NewGuid() }) }, busy);
        using var read = await Send(limited, HttpMethod.Get, url, busy);
        using var otherCustomer = await PutTravellers(limited, other, url);

        writes.ShouldBe([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests]);
        (await Problem(order)).ShouldBe((HttpStatusCode.TooManyRequests, "rate-limited")); // one write budget per customer
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound); // reads (status polling) are not counted per customer
        otherCustomer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requests_without_a_valid_token_are_limited_by_address_before_authentication()
    {
        using var limited = Limited(new() { ["RateLimiting:Anonymous:PermitLimit"] = "2" });
        var url = Travellers(Guid.NewGuid());

        var statuses = new List<HttpStatusCode>();
        foreach (var token in new[] { null, "not-a-token", null })
        {
            using var response = await SendAsync(limited, new HttpRequestMessage(HttpMethod.Get, url), token);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests]);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("PUT", "/0/document")]
    public async Task Traveller_endpoints_refuse_anonymous_requests(string method, string suffix)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Travellers(Guid.NewGuid()) + suffix) { Content = JsonContent.Create(new { }) };

        using var response = await SendAsync(api, request, null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changed_travellers_replace_the_old_rows_and_none_is_left_outside_its_order()
    {
        var token = Token();
        var order = await NewOrder("JFK", token);
        (await PutTravellers(token, order.Id, Adult())).Dispose();

        using var changed = await PutTravellers(token, order.Id, Adult("Grace"));

        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var rows = await db.Set<Traveller>().AsNoTracking().Where(t => EF.Property<Guid>(t, "OrderId") == order.Id).ToListAsync(Ct);
        rows.ShouldHaveSingleItem().GivenNames.ShouldBe("Grace");
    }

    // ---------- Helpers ----------

    /// <summary>The mock supplier's revalidation says documents are required for this destination.</summary>
    private const string _documentsRequiredDestination = "ZDR";

    private static string Travellers(Guid orderId) => $"/api/v1/orders/{orderId}/travellers";

    private static string Token() => TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private static TravellerBody Adult(string given = "Ada") => new("Adult", given, "Lovelace", "1990-12-10", "Female");

    private Task<HttpResponseMessage> PutTravellers(string? token, Guid orderId, params TravellerBody[] travellers) =>
        PutTravellers(token, orderId, travellers, phone: "+447700900123");

    private Task<HttpResponseMessage> PutTravellers(string? token, Guid orderId, TravellerBody[] travellers, string phone) =>
        SendAsync(api, new HttpRequestMessage(HttpMethod.Put, Travellers(orderId))
        {
            Content = JsonContent.Create(new { contact = new { email = "ada@example.com", phone }, travellers }),
        }, token);

    private Task<HttpResponseMessage> PutDocument(string token, Guid orderId, int position) =>
        SendAsync(api, new HttpRequestMessage(HttpMethod.Put, $"{Travellers(orderId)}/{position}/document")
        {
            Content = JsonContent.Create(new { type = "Passport", number = "P1234567", issuingCountry = "GB", nationality = "GB", expiryDate = "2040-01-01" }),
        }, token);

    private async Task<OrderTravellersReadiness> Readiness(Guid orderId, string token)
    {
        var customerId = (await Read(await Send(HttpMethod.Get, "/api/v1/customers/me", token))).GetProperty("customerId").GetString()!;
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderTravellers>().GetReadinessAsync(orderId, customerId, Ct);
    }

    private async Task<int> CountDocuments(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Set<TravelDocument>()
            .CountAsync(d => d.OrderId == orderId && d.ShreddedAt == null, Ct);
    }

    private async Task<LegalHoldOutcome> Hold(Guid orderId, bool hold)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LegalHoldHandler>()
            .HandleAsync(new LegalHoldRequest(orderId, hold, "ops-1", "disputed payment"), Ct);
    }

    private async Task Purge()
    {
        var schedule = api.Services.GetServices<BackgroundJobSchedule>().Single(s => s.Name == PurgePersonalDataJob.Name);
        using var scope = api.Services.CreateScope();
        await ((IBackgroundJob)scope.ServiceProvider.GetRequiredService(schedule.JobType)).RunOnceAsync(Ct);
    }

    // Searched, selected, revalidated (and a changed price accepted) and ordered by the customer.
    private async Task<(Guid Id, Needs Needs)> NewOrder(string destination, string token)
    {
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var search = await client.PostAsJsonAsync("/api/v1/flights/searches", new { origin = "LHR", destination, departureDate = departure }, Ct);
        var found = await Read(search);
        var searchId = found.GetProperty("searchId").GetGuid();
        var offerId = found.GetProperty("offers")[0].GetProperty("offerId").GetGuid();

        using var select = await SendAsync(api, new HttpRequestMessage(HttpMethod.Post, "/api/v1/flights/selected-offers") { Content = JsonContent.Create(new { searchId, offerId }) }, token);
        var selected = (await Read(select)).GetProperty("selectedOfferId").GetGuid();
        using var revalidated = await Send(HttpMethod.Post, $"/api/v1/flights/selected-offers/{selected}/revalidations", token);
        if (revalidated.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var quote = (await revalidated.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();
            (await SendAsync(api, new HttpRequestMessage(HttpMethod.Post, $"/api/v1/flights/selected-offers/{selected}/price-acceptances") { Content = JsonContent.Create(new { priceQuoteId = quote }) }, token)).Dispose();
        }

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(new { selectedOfferId = selected }) };
        create.Headers.Add("Idempotency-Key", $"order-{Guid.NewGuid():N}");
        using var created = await SendAsync(api, create, token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await Read(created);
        var needs = order.GetProperty("items")[0].GetProperty("travellers").Deserialize<Needs>(JsonSerializerOptions.Web)!;
        return (order.GetProperty("orderId").GetGuid(), needs);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token) => Send(api, method, url, token);

    private WebApplicationFactory<Program> Limited(Dictionary<string, string?> settings) =>
        api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings)));

    private static Task<HttpResponseMessage> PutTravellers(WebApplicationFactory<Program> host, string token, string url) =>
        SendAsync(host, new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new { contact = new { email = "ada@example.com", phone = "+447700900123" }, travellers = new[] { Adult() } }),
        }, token);

    private static Task<HttpResponseMessage> Send(WebApplicationFactory<Program> host, HttpMethod method, string url, string token) =>
        SendAsync(host, new HttpRequestMessage(method, url), token);

    private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> host, HttpRequestMessage request, string? token)
    {
        using var client = host.CreateClient();
        using (request)
        {
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await client.SendAsync(request, Ct);
        }
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TravellerBody(string Type, string GivenNames, string Surname, string DateOfBirth, string Gender);

    private sealed record Needs(int Adults, int Children, int Infants, bool DocumentsRequired);
}
