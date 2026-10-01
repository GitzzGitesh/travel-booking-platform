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

        // The payment provider's webhook: authenticated by the provider's signature, mapped only when Stripe is enabled.
        "/api/v1/payments/notifications/{providerId}",
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

    // Admin routes (ADR 0022): only staff permission policies, never anonymous or customer access, and no staff policy
    // outside the admin group.
    [Fact]
    public void Admin_routes_use_staff_permission_policies_only_and_staff_policies_stay_in_the_admin_group()
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var admin = endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/api/admin/", StringComparison.Ordinal)).ToList();

        admin.ShouldNotBeEmpty();
        admin.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() == null
            && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()
            && e.Metadata.GetOrderedMetadata<IAuthorizeData>().All(a => a.Policy != null && a.Policy.StartsWith("staff:")));
        endpoints.Except(admin)
            .ShouldAllBe(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy != null && a.Policy.StartsWith("staff:")));
    }
}
