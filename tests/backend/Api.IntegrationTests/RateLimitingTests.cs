using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Per-client rate limiting of anonymous API endpoints and trusted forwarded headers (security rules; the exposure
/// checklist in docs/progress.md). Platform-neutral: no proxy is trusted unless configured.
/// </summary>
public sealed class RateLimitingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _searchUrl = "/api/v1/flights/searches";

    // Test-only: sets the connection's address, as Kestrel would for a real socket.
    private const string _testClientHeader = "X-Test-Client-IP";

    private static readonly Dictionary<string, string> _routePolicies = new()
    {
        ["/api/v1/flights/searches"] = RateLimitPolicies.SupplierCalls,
        ["/api/v1/flights/selected-offers"] = RateLimitPolicies.Anonymous,
        ["/api/v1/flights/selected-offers/{selectedOfferId:guid}/revalidations"] = RateLimitPolicies.SupplierCalls,
        ["/api/v1/flights/selected-offers/{selectedOfferId:guid}/price-acceptances"] = RateLimitPolicies.Anonymous,
    };

    [Fact]
    public void Every_anonymous_api_endpoint_is_rate_limited_with_its_policy()
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));

        var anonymousApi = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
            .ToList();

        anonymousApi.ShouldNotBeEmpty();
        foreach (var endpoint in anonymousApi)
        {
            var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            policy.ShouldBe(_routePolicies[endpoint.RoutePattern.RawText!], endpoint.RoutePattern.RawText);
            endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>().ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_client_over_its_limit_gets_429_problem_details_while_other_clients_continue()
    {
        using var host = Host(supplierLimit: 2);
        using var client = host.CreateClient();

        (await Search(client, "203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Search(client, "203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var limited = await Search(client, "203.0.113.10");
        using var other = await Search(client, "203.0.113.20");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await limited.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!.Type.ShouldBe("rate-limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]); // security headers still apply
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IPv6_clients_are_limited_per_64_prefix_so_rotating_addresses_does_not_help()
    {
        using var host = Host(supplierLimit: 1);
        using var client = host.CreateClient();

        (await Search(client, "2001:db8:1:2::10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var sameNetwork = await Search(client, "2001:db8:1:2:ffff:ffff:ffff:1");
        using var otherNetwork = await Search(client, "2001:db8:1:3::10");

        sameNetwork.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        otherNetwork.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_IPv4_mapped_address_shares_the_IPv4_clients_bucket()
    {
        using var host = Host(supplierLimit: 1);
        using var client = host.CreateClient();

        (await Search(client, "203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var mapped = await Search(client, "::ffff:203.0.113.10");

        mapped.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Health_checks_are_not_rate_limited()
    {
        using var host = Host(supplierLimit: 1, anonymousLimit: 1);
        using var client = host.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add(_testClientHeader, "203.0.113.10");
            (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Without_a_trusted_proxy_a_forwarded_header_cannot_move_a_client_to_a_fresh_bucket()
    {
        using var host = Host(supplierLimit: 1);
        using var client = host.CreateClient();

        (await Search(client, "203.0.113.10", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var spoofed = await Search(client, "203.0.113.10", forwardedFor: "198.51.100.2");

        spoofed.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Behind_a_configured_proxy_clients_are_limited_by_their_forwarded_address()
    {
        using var host = Host(supplierLimit: 1, settings: new() { ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.4" });
        using var client = host.CreateClient();

        using var first = await Search(client, "10.0.0.4", forwardedFor: "198.51.100.1");
        using var second = await Search(client, "10.0.0.4", forwardedFor: "198.51.100.2");
        using var repeat = await Search(client, "10.0.0.4", forwardedFor: "198.51.100.1");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Only_the_nearest_proxy_hop_is_trusted()
    {
        using var host = Host(supplierLimit: 1, settings: new() { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/24" });
        using var client = host.CreateClient();

        // The client prepends a fake hop; the proxy appends the real client address, which is what counts.
        (await Search(client, "10.0.0.4", forwardedFor: "192.0.2.1, 198.51.100.7")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var sameClient = await Search(client, "10.0.0.4", forwardedFor: "192.0.2.99, 198.51.100.7");

        sameClient.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_sender_that_is_not_a_configured_proxy_is_not_trusted()
    {
        using var host = Host(supplierLimit: 1, settings: new() { ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.4" });
        using var client = host.CreateClient();

        (await Search(client, "203.0.113.10", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var spoofed = await Search(client, "203.0.113.10", forwardedFor: "198.51.100.2");

        spoofed.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-address")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99")]
    [InlineData("RateLimiting:SupplierCalls:PermitLimit", "0")]
    [InlineData("RateLimiting:Anonymous:WindowSeconds", "0")]
    public void Invalid_configuration_stops_the_host_at_startup(string key, string value)
    {
        using var host = Host(settings: new() { [key] = value });

        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public void With_no_proxy_configured_forwarded_headers_are_switched_off_not_trusted_from_anyone()
    {
        using var host = Host();

        var options = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardedHeaders.ShouldBe(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None);
    }

    private WebApplicationFactory<Program> Host(int supplierLimit = 100, int anonymousLimit = 100, Dictionary<string, string?>? settings = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            var values = new Dictionary<string, string?>
            {
                ["RateLimiting:SupplierCalls:PermitLimit"] = supplierLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:Anonymous:PermitLimit"] = anonymousLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            foreach (var (key, value) in settings ?? [])
            {
                values[key] = value;
            }

            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(values));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestClientAddress>());
        });

    private static async Task<HttpResponseMessage> Search(HttpClient client, string clientAddress, string? forwardedFor = null)
    {
        var departure = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(HttpMethod.Post, _searchUrl)
        {
            Content = JsonContent.Create(new { origin = "LHR", destination = "JFK", departureDate = departure }),
        };
        request.Headers.Add(_testClientHeader, clientAddress);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs before the app's own pipeline, so forwarded headers and the limiter see a real client address.</summary>
    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(_testClientHeader, out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
