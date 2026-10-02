using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TravelBooking.Modules.Access.Endpoints;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The admin-web session (ADR 0023), over HTTP with a real SQL Server, signed in with the Development stand-in: an
/// HttpOnly, Secure, SameSite=Strict cookie; the same permissions as staff tokens, read again on every request; the
/// CSRF header on unsafe requests; sign-out; idle and absolute lifetimes.
/// </summary>
public sealed class StaffSessionTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _session = "/api/admin/v1/session";
    private const string _queue = "/api/admin/v1/orders";

    [Fact]
    public async Task Signed_out_there_is_no_session_and_sign_in_is_unavailable_without_a_tenant()
    {
        using var host = Host();
        using var client = Client(host);

        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(_queue, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var signIn = await client.GetAsync($"{_session}/sign-in?returnUrl=/orders", Ct);
        signIn.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await signIn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("staff-sign-in-unavailable");
    }

    [Fact]
    public async Task A_session_is_a_strict_http_only_cookie_carrying_the_staff_members_permissions()
    {
        using var host = Host();
        using var client = Client(host);

        using var signedIn = await SignIn(client, TestStaffTokens.Operations);

        signedIn.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.ShouldStartWith("__host-tb-staff=");
        cookie.ShouldContain("path=/");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
        cookie.ShouldContain("httponly");
        cookie.ShouldNotContain("expires="); // a browser-session cookie

        var session = await client.GetFromJsonAsync<JsonElement>(_session, Ct);
        session.GetProperty("staffId").GetString()!.ShouldStartWith("staff:");
        session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("orders.read");
        (await client.GetAsync(_queue, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/admin/v1/access/role-grants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unsafe_requests_need_the_csrf_header_and_so_does_the_development_sign_in()
    {
        using var host = Host();
        using var client = Client(host);
        using var noHeader = await client.PostAsJsonAsync($"{_session}/development-sign-in", new { objectId = TestStaffTokens.Administrator }, Ct);
        (await noHeader.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("csrf-header-required");
        (await SignIn(client, TestStaffTokens.Administrator)).Dispose();
        var change = new { objectId = NewAccount(), role = "Privacy", action = "Grant", reason = "TICKET-900" };

        using var withoutHeader = await client.PostAsJsonAsync("/api/admin/v1/access/role-changes", change, Ct);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // the refusal ended the session

        (await SignIn(client, TestStaffTokens.Administrator)).Dispose();
        using var withHeader = await Send(client, HttpMethod.Post, "/api/admin/v1/access/role-changes", change);
        withHeader.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await withHeader.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("requestedBy").GetString()!.ShouldStartWith("staff:");
    }

    [Fact]
    public async Task Permissions_are_read_on_every_request_so_an_approved_grant_applies_without_signing_in_again()
    {
        using var host = Host();
        using var client = Client(host);
        var newcomer = NewAccount();
        (await SignIn(client, newcomer)).Dispose();
        (await client.GetAsync(_queue, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetFromJsonAsync<JsonElement>(_session, Ct)).GetProperty("permissions").GetArrayLength().ShouldBe(0);

        using var admin = Client(host);
        (await SignIn(admin, TestStaffTokens.Administrator)).Dispose();
        using var requested = await Send(admin, HttpMethod.Post, "/api/admin/v1/access/role-changes",
            new { objectId = newcomer, role = "Operations", action = "Grant", reason = "TICKET-901" });
        var requestId = (await requested.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("requestId").GetGuid();
        using var approval = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/access/role-changes/{requestId}/decision")
        {
            Content = JsonContent.Create(new { approve = true, reason = "TICKET-901" }),
        };
        approval.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestStaffTokens.For(TestStaffTokens.SecondAdministrator));
        (await api.CreateClient().SendAsync(approval, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync(_queue, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_session_and_a_staff_token_together_are_ambiguous_and_refused()
    {
        using var host = Host();
        using var client = Client(host);
        (await SignIn(client, TestStaffTokens.Operations)).Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Get, _queue);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestStaffTokens.For(TestStaffTokens.Administrator));

        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        using var host = Host();
        using var client = Client(host);
        (await SignIn(client, TestStaffTokens.Operations)).Dispose();

        using var signedOut = await Send(client, HttpMethod.Post, $"{_session}/sign-out");

        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_ends_when_idle_and_at_its_absolute_lifetime_however_active()
    {
        using var host = Host();
        using var idle = Client(host);
        (await SignIn(idle, TestStaffTokens.Operations)).Dispose();
        api.Clock.Advance(TimeSpan.FromMinutes(31));
        (await idle.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var active = Client(host);
        (await SignIn(active, TestStaffTokens.Operations)).Dispose();
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(7.9); elapsed += TimeSpan.FromMinutes(20))
        {
            api.Clock.Advance(TimeSpan.FromMinutes(20)); // each request renews the idle timeout (sliding)
            (await active.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        api.Clock.Advance(TimeSpan.FromMinutes(20));
        (await active.GetAsync(_session, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The tenant sign-in's gate (after the ID token is validated): the same refusals as for staff tokens, and a session
    // that holds the account only.
    [Fact]
    public async Task Tenant_sign_in_needs_mfa_a_staff_account_and_no_reserved_claims_and_keeps_only_the_account()
    {
        var signedIn = await SignInWithIdToken(new Claim("iss", TestStaffTokens.Issuer), new Claim("oid", TestStaffTokens.Operations), new Claim("amr", "pwd"), new Claim("amr", "mfa"), new Claim("name", "A Person"));
        signedIn.Result.ShouldBeNull();
        signedIn.Principal!.Identities.ShouldHaveSingleItem().AuthenticationType.ShouldBe(StaffIdentity.SessionScheme);
        signedIn.Principal.Claims.Select(c => (c.Type, c.Value)).ShouldBe([("iss", TestStaffTokens.Issuer), ("oid", TestStaffTokens.Operations)]);
        signedIn.Properties!.Items.ShouldContainKey("tb_signed_in_at");
        signedIn.Properties.RedirectUri.ShouldBe("/orders");

        (await SignInWithIdToken(new Claim("iss", TestStaffTokens.Issuer), new Claim("oid", TestStaffTokens.Operations), new Claim("amr", "pwd"))).Result!.Failure.ShouldNotBeNull();
        (await SignInWithIdToken(new Claim("iss", TestStaffTokens.Issuer), new Claim("amr", "mfa"))).Result!.Failure.ShouldNotBeNull();
        (await SignInWithIdToken(new Claim("iss", TestStaffTokens.Issuer), new Claim("oid", TestStaffTokens.Operations), new Claim("amr", "mfa"),
            new Claim(StaffIdentity.PermissionClaim, "access.grants.approve"))).Result!.Failure.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("https://login.test.invalid/other-tenant/v2.0", "https://login.test.invalid/staff-tenant/v2.0")]
    [InlineData("https://login.test.invalid/common/v2.0", "https://login.test.invalid/common/v2.0")]
    [InlineData("https://login.test.invalid/staff-tenant/v2.0", "")]
    public void Sign_in_uses_the_staff_tenants_own_authority_as_staff_tokens_do(string sessionAuthority, string tokenAuthority)
    {
        using var host = api.WithWebHostBuilder(b => b
            .UseSetting("Authentication:StaffSession:Authority", sessionAuthority)
            .UseSetting("Authentication:StaffSession:ClientId", "client")
            .UseSetting("Authentication:StaffSession:ClientSecret", "not-a-real-secret")
            .UseSetting("Authentication:Staff:Authority", tokenAuthority)
            .UseSetting("Authentication:Staff:RequiredScope", "staff.operations"));

        Should.Throw<InvalidOperationException>(() => host.CreateClient()).Message.ShouldContain("staff tenant's own authority");
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/orders/1?tab=timeline", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("/a\r\nb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sign_in_returns_to_local_paths_only(string? returnUrl, bool local) =>
        StaffSessionEndpoints.IsLocal(returnUrl).ShouldBe(local);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TokenValidatedContext> SignInWithIdToken(params Claim[] idTokenClaims)
    {
        using var scope = api.Services.CreateScope();
        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new AuthenticationScheme(StaffSessions.SignInScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(new ClaimsIdentity(idTokenClaims, "id_token")),
            new AuthenticationProperties { RedirectUri = "/orders" });
        await StaffSessions.SignedInAsync(context);
        return context;
    }

    private WebApplicationFactory<Program> Host() =>
        api.WithWebHostBuilder(b => b.UseSetting("Authentication:StaffSession:DevelopmentSignIn", "true"));

    // HTTPS, as the Secure cookie requires; cookies kept like a browser; no redirects followed.
    private static HttpClient Client(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });

    private static Task<HttpResponseMessage> SignIn(HttpClient client, string objectId) =>
        Send(client, HttpMethod.Post, $"{_session}/development-sign-in", new { objectId });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-TB-Staff-Csrf", "1");
        return await client.SendAsync(request, Ct);
    }

    private static string NewAccount() => Guid.NewGuid().ToString("D");
}
