using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Customer authentication as configured today, without a tenant and without the tests' signing key (ADR 0008): it fails
/// closed. A tenant cannot be half-configured: an authority without a required scope stops startup.
/// </summary>
public sealed class CustomerAuthenticationDefaultsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Without_a_configured_tenant_every_customer_token_is_refused()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestCustomerTokens.For("user-1", DateTimeOffset.UtcNow));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldNotContain("error_description"); // no reason given to the caller
    }

    [Fact]
    public void A_tenant_authority_without_a_required_scope_stops_startup()
    {
        using var misconfigured = factory.WithWebHostBuilder(b => b.UseSetting("Authentication:Customers:Authority", "https://tenant.example.invalid/v2.0"));

        Should.Throw<InvalidOperationException>(() => misconfigured.CreateClient()).Message.ShouldContain("RequiredScope");
    }
}
