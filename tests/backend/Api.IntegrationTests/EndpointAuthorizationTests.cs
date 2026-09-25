using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Deny by default (.claude/rules/security.md): every endpoint must declare an authorization policy or be
/// explicitly anonymous. Until the fallback policy arrives with authentication (Phase 4), this test is the
/// only guard, so it checks every environment in which endpoints may be mapped differently.
/// </summary>
public sealed class EndpointAuthorizationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Every_endpoint_declares_authorization(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        endpoints.ShouldNotBeEmpty();

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                && e.Metadata.GetMetadata<AuthorizationPolicy>() is null
                && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText);

        undeclared.ShouldBeEmpty();
    }

    // Anonymous access is a deliberate decision per route: a new anonymous endpoint fails here until it is added.
    // Every /api/v1 entry must be rate limited before it is mapped outside Development (docs/progress.md).
    private static readonly string[] _anonymousRoutes =
    [
        "/api/v1/flights/searches",
        "/api/v1/flights/selected-offers",
        "/api/v1/flights/selected-offers/{selectedOfferId:guid}/revalidations",
        "/api/v1/flights/selected-offers/{selectedOfferId:guid}/price-acceptances",
    ];

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Only_allow_listed_api_routes_are_anonymous(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));

        var anonymous = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(route => route.StartsWith("/api/", StringComparison.Ordinal))
            .Distinct();

        anonymous.ShouldBeSubsetOf(_anonymousRoutes);
    }
}
