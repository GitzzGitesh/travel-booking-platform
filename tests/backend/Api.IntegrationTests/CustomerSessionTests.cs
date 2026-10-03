using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers;
using TravelBooking.Modules.Customers.Endpoints;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The customer-web session (ADR 0028), over HTTP with a real SQL Server, signed in with the Development stand-in: an
/// HttpOnly, Secure, SameSite=Lax cookie holding the account only; our customer id mapped on every request; the CSRF
/// header on unsafe requests; never together with a token; sign-out; idle and absolute lifetimes.
/// </summary>
public sealed class CustomerSessionTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _session = "/api/v1/session";
    private const string _me = "/api/v1/customers/me";

    [Fact]
    public async Task Signed_out_there_is_no_session_and_sign_in_is_unavailable_without_a_tenant()
    {
        using var host = Host();
        using var client = Client(host);

        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(_me, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var signIn = await client.GetAsync($"{_session}/sign-in?returnUrl=/trips", Ct);
        signIn.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await signIn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("customer-sign-in-unavailable");
    }

    [Fact]
    public async Task A_session_is_a_lax_http_only_cookie_for_one_customer_who_stays_the_same()
    {
        using var host = Host();
        using var client = Client(host);
        var account = NewAccount();

        using var signedIn = await SignIn(client, account);

        signedIn.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.ShouldStartWith("__host-tb-customer=");
        cookie.ShouldContain("path=/");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=lax");
        cookie.ShouldContain("httponly");
        cookie.ShouldNotContain("expires="); // a browser-session cookie

        var customerId = (await client.GetFromJsonAsync<JsonElement>(_session, Ct)).GetProperty("customerId").GetString();
        customerId.ShouldNotBeNullOrEmpty();
        (await client.GetAsync(_me, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var again = Client(host);
        (await SignIn(again, account)).Dispose();
        (await again.GetFromJsonAsync<JsonElement>(_session, Ct)).GetProperty("customerId").GetString().ShouldBe(customerId); // one account, one customer
        using var other = Client(host);
        (await SignIn(other, NewAccount())).Dispose();
        (await other.GetFromJsonAsync<JsonElement>(_session, Ct)).GetProperty("customerId").GetString().ShouldNotBe(customerId);
    }

    [Fact]
    public async Task Unsafe_requests_need_the_csrf_header_and_so_does_the_development_sign_in()
    {
        using var host = Host();
        using var client = Client(host);
        using var noHeader = await client.PostAsJsonAsync($"{_session}/development-sign-in", new { objectId = NewAccount() }, Ct);
        (await noHeader.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("csrf-header-required");
        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // nobody was signed in

        (await SignIn(client, NewAccount())).Dispose();
        using var withoutHeader = await client.PostAsync($"{_session}/sign-out", null, Ct);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // the refusal ended the session
    }

    [Fact]
    public async Task A_session_and_a_customer_token_together_are_ambiguous_and_refused()
    {
        using var host = Host();
        using var client = Client(host);
        (await SignIn(client, NewAccount())).Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Get, _me);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow));

        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // authenticated, but no single customer
    }

    [Fact]
    public async Task A_staff_session_is_not_a_customer_and_a_customer_session_is_not_staff()
    {
        using var host = api.WithWebHostBuilder(b => b
            .UseSetting("Authentication:CustomerSession:DevelopmentSignIn", "true")
            .UseSetting("Authentication:StaffSession:DevelopmentSignIn", "true"));
        using var customer = Client(host);
        (await SignIn(customer, NewAccount())).Dispose();
        (await customer.GetAsync("/api/admin/v1/session", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await customer.GetAsync("/api/admin/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var staff = Client(host);
        using var staffSignIn = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/session/development-sign-in") { Content = JsonContent.Create(new { objectId = TestStaffTokens.Administrator }) };
        staffSignIn.Headers.Add(StaffIdentity.CsrfHeader, "1");
        (await staff.SendAsync(staffSignIn, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await staff.GetAsync(_me, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await staff.GetAsync($"/api/v1/orders/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task One_customers_session_never_reaches_another_customers_selection_order_or_travellers()
    {
        using var host = Host();
        using var owner = Client(host);
        (await SignIn(owner, NewAccount())).Dispose();
        var selectedOfferId = await Selected(owner);
        using var revalidated = await Send(owner, HttpMethod.Post, $"/api/v1/flights/selected-offers/{selectedOfferId}/revalidations");
        revalidated.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var order = await Send(owner, HttpMethod.Post, "/api/v1/orders", new { selectedOfferId }, Guid.NewGuid().ToString("N"));
        order.StatusCode.ShouldBe(HttpStatusCode.Created);
        var orderId = (await order.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("orderId").GetGuid();
        (await owner.GetAsync($"/api/v1/orders/{orderId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var other = Client(host);
        (await SignIn(other, NewAccount())).Dispose();
        (await other.GetAsync($"/api/v1/orders/{orderId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/v1/orders/{orderId}/travellers", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var foreign = await Send(other, HttpMethod.Post, $"/api/v1/flights/selected-offers/{selectedOfferId}/revalidations");
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Sign_in_uses_the_customer_tenants_own_authority_as_customer_tokens_do()
    {
        using var host = api.WithWebHostBuilder(b => b
            .UseSetting("Authentication:CustomerSession:Authority", "https://login.test.invalid/common/v2.0")
            .UseSetting("Authentication:CustomerSession:ClientId", "client")
            .UseSetting("Authentication:CustomerSession:ClientSecret", "not-a-real-secret"));

        Should.Throw<InvalidOperationException>(() => host.CreateClient()).Message.ShouldContain("customer tenant's own authority");
    }

    [Fact]
    public async Task An_endpoint_open_to_anonymous_callers_acts_for_the_session_and_never_treats_a_refused_one_as_anonymous()
    {
        using var host = Host();
        using var client = Client(host);
        (await SignIn(client, NewAccount())).Dispose();
        (await Selected(client)).ShouldNotBe(Guid.Empty);

        using var withoutHeader = await client.PostAsJsonAsync("/api/v1/flights/selected-offers", await SearchedOffer(client), Ct);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // refused, never anonymous
    }

    private async Task<object> SearchedOffer(HttpClient client)
    {
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var searched = await Send(client, HttpMethod.Post, "/api/v1/flights/searches", new { origin = "LHR", destination = "JFK", departureDate = departure });
        var search = await searched.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new { searchId = search.GetProperty("searchId").GetGuid(), offerId = search.GetProperty("offers")[0].GetProperty("offerId").GetGuid() };
    }

    private async Task<Guid> Selected(HttpClient client)
    {
        using var selected = await Send(client, HttpMethod.Post, "/api/v1/flights/selected-offers", await SearchedOffer(client));
        selected.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await selected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("selectedOfferId").GetGuid();
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        using var host = Host();
        using var client = Client(host);
        (await SignIn(client, NewAccount())).Dispose();

        using var signedOut = await Send(client, HttpMethod.Post, $"{_session}/sign-out");

        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_ends_when_idle_and_at_its_absolute_lifetime_however_active()
    {
        using var host = Host();
        using var idle = Client(host);
        (await SignIn(idle, NewAccount())).Dispose();
        api.Clock.Advance(TimeSpan.FromMinutes(61));
        (await idle.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var active = Client(host);
        (await SignIn(active, NewAccount())).Dispose();
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(11.5); elapsed += TimeSpan.FromMinutes(30))
        {
            api.Clock.Advance(TimeSpan.FromMinutes(30)); // each request renews the idle timeout (sliding)
            (await active.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        api.Clock.Advance(TimeSpan.FromMinutes(31));
        (await active.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The tenant sign-in's gate (after the ID token is validated): the same refusals as for customer tokens, and a
    // session that holds the account only.
    [Fact]
    public async Task Tenant_sign_in_needs_a_customer_account_and_no_reserved_claims_and_keeps_only_the_account()
    {
        var account = NewAccount();
        var signedIn = await SignInWithIdToken(new Claim("iss", "https://login.test.invalid/customers/v2.0"), new Claim("oid", account), new Claim("name", "A Person"));
        signedIn.Result.ShouldBeNull();
        signedIn.Principal!.Identities.ShouldHaveSingleItem().AuthenticationType.ShouldBe(CustomerIdentity.SessionScheme);
        signedIn.Principal.Claims.Select(c => (c.Type, c.Value)).ShouldBe([("iss", "https://login.test.invalid/customers/v2.0"), ("oid", account)]);
        signedIn.Properties!.Items.ShouldContainKey("tb_signed_in_at");
        signedIn.Properties.RedirectUri.ShouldBe("/trips");

        (await SignInWithIdToken(new Claim("iss", "https://login.test.invalid/customers/v2.0"))).Result!.Failure.ShouldNotBeNull();
        (await SignInWithIdToken(new Claim("iss", "https://login.test.invalid/customers/v2.0"), new Claim("oid", account), new Claim("idtyp", "app")))
            .Result!.Failure.ShouldNotBeNull();
        (await SignInWithIdToken(new Claim("iss", "https://login.test.invalid/customers/v2.0"), new Claim("oid", account),
            new Claim(CustomerIdentity.CustomerIdClaim, "someone-else"))).Result!.Failure.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/trips?tab=upcoming", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("/a\r\nb", false)]
    [InlineData(null, false)]
    public void Sign_in_returns_to_local_paths_only(string? returnUrl, bool local) =>
        CustomerSessionEndpoints.IsLocal(returnUrl).ShouldBe(local);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TokenValidatedContext> SignInWithIdToken(params Claim[] idTokenClaims)
    {
        using var scope = api.Services.CreateScope();
        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new AuthenticationScheme(CustomerSessions.SignInScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(new ClaimsIdentity(idTokenClaims, "id_token")),
            new AuthenticationProperties { RedirectUri = "/trips" });
        await CustomerSessions.SignedInAsync(context);
        return context;
    }

    private WebApplicationFactory<Program> Host() =>
        api.WithWebHostBuilder(b => b.UseSetting("Authentication:CustomerSession:DevelopmentSignIn", "true"));

    // HTTPS, as the Secure cookie requires; cookies kept like a browser; no redirects followed.
    private static HttpClient Client(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });

    private static Task<HttpResponseMessage> SignIn(HttpClient client, string objectId) =>
        Send(client, HttpMethod.Post, $"{_session}/development-sign-in", new { objectId });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add(CustomerIdentity.CsrfHeader, "1");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string NewAccount() => Guid.NewGuid().ToString("D");
}
