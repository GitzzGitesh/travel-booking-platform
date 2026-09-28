using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.Modules.Customers.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Customer identity and the customer's orders over HTTP (ADR 0008, Q8): customer tokens are validated for real (signed,
/// issuer, audience, lifetime, RS256 only), the provider's subject maps to one internal customer id, and every order
/// endpoint acts only on the signed-in customer's own orders. The permission matrix: anonymous is refused, an invalid
/// token is refused, customer A never sees or changes customer B's order.
/// </summary>
public sealed class CustomerOrderEndpointTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _orders = "/api/v1/orders";

    // ---------- Authentication ----------

    [Theory]
    [InlineData("GET", "/api/v1/customers/me")]
    [InlineData("POST", "/api/v1/orders")]
    [InlineData("GET", "/api/v1/orders/9f3c1f0e-5d3b-4a55-9f86-2f6d3a0b1c11")]
    public async Task Customer_endpoints_refuse_anonymous_requests(string method, string url)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "POST" ? JsonContent.Create(new { selectedOfferId = Guid.NewGuid() }) : null };

        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> InvalidTokens => new()
    {
        "wrong-key", "expired", "wrong-audience", "wrong-issuer", "symmetric-algorithm", "no-object-id", "not-a-token",
        "own-customer-id-claim", "own-customer-id-claim-other-case", "app-only-token", "object-id-with-space",
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_customer_tokens_are_refused(string kind)
    {
        var now = DateTimeOffset.UtcNow;
        var token = kind switch
        {
            "wrong-key" => TestCustomerTokens.For("user-1", now, wrongKey: true),
            "expired" => TestCustomerTokens.For("user-1", now, lifetime: TimeSpan.FromMinutes(-10)),
            "wrong-audience" => TestCustomerTokens.For("user-1", now, audience: "api://someone-else"),
            "wrong-issuer" => TestCustomerTokens.For("user-1", now, issuer: "https://attacker.invalid/v2.0"),
            "symmetric-algorithm" => TestCustomerTokens.For("user-1", now, algorithm: SecurityAlgorithms.HmacSha256),
            "no-object-id" => TestCustomerTokens.For(null, now),

            // Validly signed tokens that try to choose their own internal customer id, in any spelling.
            "own-customer-id-claim" => TestCustomerTokens.For("user-1", now, extraClaims: [new Claim("tb_customer_id", new string('a', 32))]),
            "own-customer-id-claim-other-case" => TestCustomerTokens.For("user-1", now, extraClaims: [new Claim("TB_Customer_Id", new string('a', 32))]),

            // A customer is a user: an app-only (client credentials) token is not one.
            "app-only-token" => TestCustomerTokens.For("user-1", now, extraClaims: [new Claim("idtyp", "app")]),
            "object-id-with-space" => TestCustomerTokens.For("user 1", now),
            _ => "not.a.token",
        };

        (await Send(HttpMethod.Get, "/api/v1/customers/me", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_token_maps_the_account_to_one_stable_internal_customer_id()
    {
        var objectId = $"object-{Guid.NewGuid():N}";

        var first = await Me(objectId);
        var again = await Me(objectId);
        var someoneElse = await Me($"object-{Guid.NewGuid():N}");
        var otherCase = await Me(objectId.ToUpperInvariant()); // exact comparison: another account

        first.ShouldMatch("^[0-9a-f]{32}$"); // ours, never the provider's id
        again.ShouldBe(first);
        someoneElse.ShouldNotBe(first);
        otherCase.ShouldNotBe(first);
        using var scope = api.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Customers.CountAsync(c => c.IdentitySubject == objectId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task When_a_scope_is_required_a_token_without_it_is_refused()
    {
        using var scoped = api.WithWebHostBuilder(b => b.UseSetting("Authentication:Customers:RequiredScope", "orders.readwrite"));
        using var client = scoped.CreateClient();
        var objectId = $"object-{Guid.NewGuid():N}";

        async Task<HttpStatusCode> MeWith(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        (await MeWith(TestCustomerTokens.For(objectId, DateTimeOffset.UtcNow))).ShouldBe(HttpStatusCode.Forbidden);
        (await MeWith(TestCustomerTokens.For(objectId, DateTimeOffset.UtcNow, extraClaims: [new Claim("scp", "profile orders.readwrite")]))).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_create_one_customer()
    {
        var subject = $"subject-{Guid.NewGuid():N}";

        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Me(subject)));

        ids.Distinct().ShouldHaveSingleItem();
    }

    // ---------- Orders ----------

    [Fact]
    public async Task A_customer_orders_a_confirmed_selection_at_its_agreed_price_and_replays_safely()
    {
        var token = Token();
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();

        using var created = await CreateOrder(token, selection, key);
        using var replay = await CreateOrder(token, selection, key);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await Read(created);
        (await Read(replay)).GetRawText().ShouldBe(order.GetRawText()); // the same order, nothing changed since
        order.GetProperty("status").GetString().ShouldBe("AwaitingPayment");
        var item = order.GetProperty("items")[0];
        item.GetProperty("selectedOfferId").GetGuid().ShouldBe(selection);
        item.GetProperty("agreedPrice").GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String);
        order.TryGetProperty("customerId", out _).ShouldBeFalse();
        created.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{order.GetProperty("orderId").GetGuid()}");
        (await Send(HttpMethod.Get, created.Headers.Location.ToString(), token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Customer_B_can_neither_see_nor_take_over_customer_As_order()
    {
        var alice = Token();
        var bob = Token();
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();
        using var created = await CreateOrder(alice, selection, key);
        var orderId = (await Read(created)).GetProperty("orderId").GetGuid();

        (await Send(HttpMethod.Get, $"{_orders}/{orderId}", bob)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Bob's own key space: his request for Alice's selection is refused without revealing her order.
        using var taken = await CreateOrder(bob, selection, key);
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = (await taken.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        problem.Type.ShouldBe("selection-already-ordered");
        problem.Extensions.ContainsKey("orderId").ShouldBeFalse();
    }

    [Fact]
    public async Task Parallel_order_requests_with_one_key_create_one_order()
    {
        var token = Token();
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateOrder(token, selection, key)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        (await Task.WhenAll(responses.Select(Read))).Select(o => o.GetProperty("orderId").GetGuid()).Distinct().ShouldHaveSingleItem();
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task The_same_key_for_another_selection_and_a_missing_key_are_refused()
    {
        var token = Token();
        var key = NewKey();
        using var first = await CreateOrder(token, await ConfirmedSelection("JFK"), key);

        using var reused = await CreateOrder(token, await ConfirmedSelection("JFK"), key);
        using var missing = await CreateOrder(token, await ConfirmedSelection("JFK"), key: null);

        (await Problem(reused)).ShouldBe((HttpStatusCode.Conflict, "idempotency-conflict"));
        (await Problem(missing)).ShouldBe((HttpStatusCode.BadRequest, "idempotency-key-required"));
    }

    [Fact]
    public async Task A_selection_whose_price_was_not_checked_or_whose_change_was_not_accepted_cannot_be_ordered()
    {
        var token = Token();

        using var unchecked_ = await CreateOrder(token, await Select("JFK"), NewKey());
        var changed = await Select("ZPC");
        using (var client = api.CreateClient())
        {
            (await client.PostAsync($"/api/v1/flights/selected-offers/{changed}/revalidations", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using var notAccepted = await CreateOrder(token, changed, NewKey());

        (await Problem(unchecked_)).ShouldBe((HttpStatusCode.Conflict, "price-check-required"));
        (await Problem(notAccepted)).ShouldBe((HttpStatusCode.Conflict, "price-check-required"));
    }

    [Fact]
    public async Task An_accepted_price_change_is_ordered_at_the_accepted_price_with_its_evidence()
    {
        using var created = await CreateOrder(Token(), await ConfirmedSelection("ZPC"), NewKey());

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Read(created)).GetProperty("items")[0].GetProperty("priceChangeAccepted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_offer_cannot_be_ordered()
    {
        var selection = await ConfirmedSelection("JFK");
        api.Clock.Advance(TimeSpan.FromHours(2)); // past the mock offer's lifetime

        using var expired = await CreateOrder(Token(), selection, NewKey());

        (await Problem(expired)).ShouldBe((HttpStatusCode.UnprocessableEntity, "offer-expired"));
    }

    [Fact]
    public async Task A_sold_out_or_unknown_selection_cannot_be_ordered()
    {
        var token = Token();
        var soldOut = await Select("ZSO");
        using (var client = api.CreateClient())
        {
            (await client.PostAsync($"/api/v1/flights/selected-offers/{soldOut}/revalidations", null, Ct)).Dispose();
        }

        using var gone = await CreateOrder(token, soldOut, NewKey());
        using var unknown = await CreateOrder(token, Guid.NewGuid(), NewKey());

        (await Problem(gone)).ShouldBe((HttpStatusCode.UnprocessableEntity, "sold-out"));
        (await Problem(unknown)).ShouldBe((HttpStatusCode.UnprocessableEntity, "offer-expired"));
    }

    [Fact]
    public async Task F32_one_selection_ordered_under_two_keys_is_one_order_and_the_second_key_is_told_which()
    {
        var token = Token();
        var selection = await ConfirmedSelection("JFK");
        using var first = await CreateOrder(token, selection, NewKey());
        var orderId = (await Read(first)).GetProperty("orderId").GetGuid();

        using var second = await CreateOrder(token, selection, NewKey());

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = (await second.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        problem.Type.ShouldBe("selection-already-ordered");
        problem.Extensions["orderId"].ShouldBeOfType<JsonElement>().GetGuid().ShouldBe(orderId); // the caller's own order
    }

    [Fact]
    public async Task F32_parallel_requests_with_different_keys_for_one_selection_create_one_order()
    {
        var token = Token();
        var selection = await ConfirmedSelection("JFK");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateOrder(token, selection, NewKey())));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(4);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Keys_belong_to_one_customer_another_customer_may_use_the_same_key()
    {
        var key = NewKey();
        using var alices = await CreateOrder(Token(), await ConfirmedSelection("JFK"), key);

        using var bobs = await CreateOrder(Token(), await ConfirmedSelection("JFK"), key);

        (alices.StatusCode, bobs.StatusCode).ShouldBe((HttpStatusCode.Created, HttpStatusCode.Created));
    }

    [Fact]
    public async Task A_malformed_key_or_request_is_a_client_error()
    {
        var token = Token();
        using var tooLong = await CreateOrder(token, await ConfirmedSelection("JFK"), new string('k', 101));
        using var withSpace = await CreateOrder(token, await ConfirmedSelection("JFK"), "order key");
        using var noSelection = await SendAsync(new HttpRequestMessage(HttpMethod.Post, _orders) { Content = JsonContent.Create(new { }), Headers = { { "Idempotency-Key", NewKey() } } }, token);

        (await Problem(tooLong)).ShouldBe((HttpStatusCode.BadRequest, "idempotency-key-required"));
        (await Problem(withSpace)).ShouldBe((HttpStatusCode.BadRequest, "idempotency-key-required"));
        noSelection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------- Helpers ----------

    // Issued at real time: JwtBearer checks token lifetimes against the system clock, not the tests' fake clock (which
    // the expired-offer test moves forward).
    private static string Token(string? objectId = null) => TestCustomerTokens.For(objectId ?? $"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private async Task<string> Me(string objectId)
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/customers/me", Token(objectId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await Read(response)).GetProperty("customerId").GetString()!;
    }

    private Task<HttpResponseMessage> CreateOrder(string token, Guid selectedOfferId, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _orders) { Content = JsonContent.Create(new { selectedOfferId }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return SendAsync(request, token);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token) => SendAsync(new HttpRequestMessage(method, url), token);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string token)
    {
        using var client = api.CreateClient();
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, Ct);
        }
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

    private async Task<Guid> ConfirmedSelection(string destination)
    {
        var selected = await Select(destination);
        using var client = api.CreateClient();
        using var revalidated = await client.PostAsync($"/api/v1/flights/selected-offers/{selected}/revalidations", null, Ct);
        if (revalidated.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var problem = (await revalidated.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
            var quote = problem.Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();
            using var accepted = await client.PostAsJsonAsync($"/api/v1/flights/selected-offers/{selected}/price-acceptances", new { priceQuoteId = quote }, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        else
        {
            revalidated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return selected;
    }

    private async Task<Guid> Select(string destination)
    {
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var search = await client.PostAsJsonAsync("/api/v1/flights/searches", new { origin = "LHR", destination, departureDate = departure }, Ct);
        var found = (await search.Content.ReadFromJsonAsync<SearchResult>(JsonSerializerOptions.Web, Ct))!;
        using var select = await client.PostAsJsonAsync("/api/v1/flights/selected-offers", new { found.SearchId, found.Offers[0].OfferId }, Ct);
        select.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await select.Content.ReadFromJsonAsync<Selection>(JsonSerializerOptions.Web, Ct))!.SelectedOfferId;
    }

    private static string NewKey() => $"order-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Offer(Guid OfferId);

    private sealed record SearchResult(Guid SearchId, IReadOnlyList<Offer> Offers);

    private sealed record Selection(Guid SelectedOfferId);
}
