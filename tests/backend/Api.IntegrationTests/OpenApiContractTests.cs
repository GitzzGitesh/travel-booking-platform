using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The OpenAPI document is the API contract (api-design rules). Any change to it must be reviewed and committed
/// as src/backend/Hosts/Api/openapi.v1.json. To accept a reviewed change, run the tests with UPDATE_OPENAPI_SNAPSHOT=1.
/// </summary>
public sealed class OpenApiContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _documentUrl = "/openapi/v1.json";

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task Document_matches_the_committed_snapshot()
    {
        using var client = factory.CreateClient();
        var actual = Normalize(await client.GetStringAsync(_documentUrl, TestContext.Current.CancellationToken));
        var snapshotPath = Path.Combine(RepositoryRoot(), "src", "backend", "Hosts", "Api", "openapi.v1.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Environment.GetEnvironmentVariable("CI").ShouldBeNullOrEmpty("The contract snapshot must never be rewritten in CI.");
            await File.WriteAllTextAsync(snapshotPath, actual, TestContext.Current.CancellationToken);
        }

        var expected = File.Exists(snapshotPath)
            ? Normalize(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken))
            : string.Empty;

        actual.ShouldBe(expected, "The OpenAPI contract changed. Review it, then rerun with UPDATE_OPENAPI_SNAPSHOT=1 and commit the snapshot.");
    }

    [Fact]
    public async Task Validation_constraints_and_problem_responses_are_documented()
    {
        using var client = factory.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync(_documentUrl, TestContext.Current.CancellationToken))!;

        var request = document["components"]!["schemas"]!["SampleRequest"]!["properties"]!;
        request["name"]!["minLength"]!.GetValue<int>().ShouldBe(1);
        request["name"]!["maxLength"]!.GetValue<int>().ShouldBe(50);
        request["quantity"]!["minimum"]!.GetValue<int>().ShouldBe(1);
        request["quantity"]!["maximum"]!.GetValue<int>().ShouldBe(9);

        var responses = document["paths"]!["/api/v1/sample/validation"]!["post"]!["responses"]!.AsObject();
        responses.Select(r => r.Key).ShouldBe(["200", "400"], ignoreOrder: true);
        responses["400"]!["content"]!.AsObject().ShouldContainKey("application/problem+json");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("Prod")]
    public async Task Document_is_served_only_in_development(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(_documentUrl, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(_indented).ReplaceLineEndings("\n") + "\n";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TravelBooking.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (TravelBooking.slnx) not found.");
    }
}
