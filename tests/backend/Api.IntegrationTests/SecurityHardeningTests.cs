using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Transport hardening from .claude/rules/security.md: security headers on every response, host filtering,
/// and an explicit CORS allow-list with no wildcards or credentials.
/// </summary>
public sealed class SecurityHardeningTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _allowedOrigin = "https://app.example.test";

    [Theory]
    [InlineData("/health", HttpStatusCode.OK)]
    [InlineData("/does-not-exist", HttpStatusCode.NotFound)]
    public async Task Security_headers_are_sent_on_success_and_error_responses(string path, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expectedStatus);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'none'; frame-ancestors 'none'");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
    }

    [Fact]
    public async Task Requests_for_an_unknown_host_are_rejected()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "attacker.example";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_cross_origin_access_is_granted_by_default()
    {
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(_allowedOrigin), TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Configured_origins_are_allowed_without_credentials_and_others_are_not()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", _allowedOrigin));
        using var client = configured.CreateClient();

        using var allowed = await client.SendAsync(Preflight(_allowedOrigin), TestContext.Current.CancellationToken);
        using var other = await client.SendAsync(Preflight("https://evil.example.test"), TestContext.Current.CancellationToken);

        Header(allowed, "Access-Control-Allow-Origin").ShouldBe(_allowedOrigin);
        allowed.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        other.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    [InlineData("http://app.example.test")]
    [InlineData("https://app.example.test/")]
    [InlineData("https://app.example.test/path")]
    [InlineData("not-a-url")]
    [InlineData("https://app.example.test@evil.example.test")]
    [InlineData("https://app.example.test@évil.example.test")]
    [InlineData("https://évil.example.test")]
    [InlineData("https://app.example.test:443")]
    [InlineData("https://APP.example.test")]
    [InlineData(" https://app.example.test")]
    public void Invalid_origins_fail_startup(string origin)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", origin));

        var exception = Should.Throw<Exception>(() => configured.CreateClient());

        exception.ToString().ShouldContain("Cors:AllowedOrigins");
    }

    [Theory]
    [InlineData("https://app.example.test")]
    [InlineData("https://app.example.test:8443")]
    [InlineData("http://localhost:4200")]
    public void Exact_origins_start_successfully(string origin)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", origin));

        Should.NotThrow(() => configured.CreateClient().Dispose());
    }

    [Fact]
    public void Plain_http_localhost_origins_are_refused_outside_development()
    {
        using var production = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Cors:AllowedOrigins:0", "http://localhost:4200"));

        var exception = Should.Throw<Exception>(() => production.CreateClient());

        exception.ToString().ShouldContain("Cors:AllowedOrigins");
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;
}
