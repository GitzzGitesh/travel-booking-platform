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
}
