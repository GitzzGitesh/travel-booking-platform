using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Validation spike (ADR 0003): the endpoint and its request type live in the Modules.Sample library,
/// and built-in .NET 10 validation must still reject invalid requests with a 400 ValidationProblem.
/// </summary>
public sealed class SampleValidationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _url = "/api/v1/sample/validation";

    [Fact]
    public async Task Valid_request_reaches_the_handler()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { name = "spike", quantity = 2 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Attribute_rules_are_enforced()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { name = "", quantity = 42 }, TestContext.Current.CancellationToken);

        var problem = await ReadValidationProblem(response);
        problem.Errors.Keys.ShouldBe(["Name", "Quantity"], ignoreOrder: true);
    }

    [Fact]
    public async Task Cross_field_rule_is_enforced()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { name = "spike", quantity = 1, from = "2026-10-02", to = "2026-10-01" }, TestContext.Current.CancellationToken);

        var problem = await ReadValidationProblem(response);
        problem.Errors.Keys.ShouldBe(["To"]);
    }

    [Fact]
    public async Task Numbers_sent_as_strings_are_rejected()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("""{ "name": "spike", "quantity": "2" }""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(_url, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Sample_module_is_not_exposed_outside_development()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = production.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { name = "spike", quantity = 2 }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<HttpValidationProblemDetails> ReadValidationProblem(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestContext.Current.CancellationToken);
        return problem.ShouldNotBeNull();
    }
}
