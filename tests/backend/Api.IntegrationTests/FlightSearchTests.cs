using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// POST /api/v1/flights/searches end to end: HTTP → Flights module → IFlightProvider → deterministic mock (ADR 0004).
/// Provider scenarios are selected through the mock's configuration, as a deployment would.
/// </summary>
public sealed class FlightSearchTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string _url = "/api/v1/flights/searches";

    private static readonly string _inThirtyDays = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd");
    private static readonly string _inThirtySevenDays = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(37).ToString("yyyy-MM-dd");

    [Fact]
    public async Task One_way_search_returns_priced_offers_with_local_times()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { origin = "LHR", destination = "JFK", departureDate = _inThirtyDays }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var offers = (await ReadJson(response))["offers"]!.AsArray();
        offers.Count.ShouldBe(3);
        foreach (var offer in offers)
        {
            // Money amounts are JSON strings; no supplier offer token is exposed.
            offer!["totalPrice"]!["amount"]!.GetValueKind().ShouldBe(System.Text.Json.JsonValueKind.String);
            offer["totalPrice"]!["currency"]!.GetValue<string>().ShouldBe("XTS");
            offer.AsObject().ContainsKey("reference").ShouldBeFalse();
            var segment = offer["slices"]!.AsArray().ShouldHaveSingleItem()!["segments"]!.AsArray()[0]!;
            segment["origin"]!.GetValue<string>().ShouldBe("LHR");
            segment["destination"]!.GetValue<string>().ShouldBe("JFK");
            segment["departureLocal"]!.GetValue<string>().ShouldStartWith(_inThirtyDays);
            segment["departureLocal"]!.GetValue<string>().ShouldNotContain("Z");
        }
    }

    [Fact]
    public async Task Round_trip_search_in_business_returns_outbound_and_return_slices()
    {
        using var client = factory.CreateClient();
        var search = new { origin = "LHR", destination = "JFK", departureDate = _inThirtyDays, returnDate = _inThirtySevenDays, adults = 2, children = 1, infants = 1, cabin = "Business" };

        using var response = await client.PostAsJsonAsync(_url, search, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var slices = (await ReadJson(response))["offers"]!.AsArray()[0]!["slices"]!.AsArray();
        slices.Count.ShouldBe(2);
        slices[1]!["segments"]!.AsArray()[0]!["origin"]!.GetValue<string>().ShouldBe("JFK");
    }

    [Theory]
    [InlineData("""{ "origin": "lhr", "destination": "JFK", "departureDate": "2099-01-01" }""", "Origin")]
    [InlineData("""{ "destination": "JFK", "departureDate": "2099-01-01" }""", "Origin")]
    [InlineData("""{ "origin": "LHR", "destination": "LHR", "departureDate": "2099-01-01" }""", "Destination")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK" }""", "DepartureDate")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "2099-01-10", "returnDate": "2099-01-09" }""", "ReturnDate")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "2099-01-01", "adults": 0 }""", "Adults")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "2099-01-01", "adults": 1, "infants": 2 }""", "Infants")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "2099-01-01", "adults": 8, "children": 2 }""", "Children")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "2020-01-01" }""", "DepartureDate")]
    [InlineData("""{ "origin": "LHR", "destination": "JFK", "departureDate": "9999-12-31" }""", "DepartureDate")]
    public async Task Invalid_searches_are_rejected_with_a_validation_problem(string json, string field)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(_url, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestContext.Current.CancellationToken);
        problem!.Errors.Keys.ShouldContain(field);
    }

    // Only documented enum names are accepted: no unknown names, no integers (defined or not), no numeric strings.
    [Theory]
    [InlineData("\"Sleeper\"")]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("\"2\"")]
    public async Task Cabins_outside_the_documented_names_are_rejected(string cabinJson)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent($$"""{ "origin": "LHR", "destination": "JFK", "departureDate": "{{_inThirtyDays}}", "cabin": {{cabinJson}} }""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(_url, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_availability_is_an_empty_success()
    {
        using var client = WithScenario("NoResults").CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { origin = "LHR", destination = "JFK", departureDate = _inThirtyDays }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJson(response))["offers"]!.AsArray().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("RateLimited")]
    public async Task Provider_outages_are_503_problems_without_supplier_details(string scenario)
    {
        using var client = WithScenario(scenario).CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { origin = "LHR", destination = "JFK", departureDate = _inThirtyDays }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem!.Type.ShouldBe("provider-unavailable");
        problem.Extensions.ShouldContainKey("traceId");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("scenario", Case.Insensitive);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Flight_search_is_not_exposed_outside_development(string environment)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(_url, new { origin = "LHR", destination = "JFK", departureDate = _inThirtyDays }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private WebApplicationFactory<Program> WithScenario(string scenario) =>
        factory.WithWebHostBuilder(b => b.UseSetting("Integrations:Flights:Mock:Scenario", scenario));

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
}
