using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TravelBooking.Integrations.Hotels.Mock;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Hotels over HTTP with a real SQL Server and the deterministic mock (ADR 0030): search, selection (idempotent per
/// caller), revalidation with the F-01..F-03 outcomes, price acceptance, ownership, and no supplier internals exposed.
/// </summary>
public sealed class HotelSearchSelectionTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _searches = "/api/v1/hotels/searches";
    private const string _selections = "/api/v1/hotels/selected-offers";

    [Fact]
    public async Task A_search_returns_prepaid_offers_with_their_cancellation_terms_and_no_supplier_ids()
    {
        using var response = await Send(HttpMethod.Post, _searches, body: Search("PAR"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("mock-1"); // the supplier's property id
        raw.ShouldNotContain("v1|"); // the adapter's offer token
        var search = JsonSerializer.Deserialize<JsonElement>(raw);
        search.GetProperty("nights").GetInt32().ShouldBe(3);
        var offers = search.GetProperty("offers").EnumerateArray().ToList();
        offers.Count.ShouldBe(3);
        offers.ShouldContain(o => !o.GetProperty("cancellation").GetProperty("refundable").GetBoolean());
        offers.ShouldContain(o => o.GetProperty("feesAtProperty").ValueKind == JsonValueKind.Object);
        offers[0].GetProperty("totalPrice").GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String); // never a JSON number
    }

    [Theory]
    [InlineData("par", null, null)] // not an upper-case IATA city code
    [InlineData("PAR", 0, null)] // check-out not after check-in
    [InlineData("PAR", 3, 18)] // a child age above 17
    public async Task An_invalid_search_is_a_400_before_any_supplier_call(string destination, int? nightsOverride, int? childAge)
    {
        var checkIn = Today.AddDays(40);
        using var response = await Send(HttpMethod.Post, _searches, body: new
        {
            destination,
            checkIn = checkIn.ToString("yyyy-MM-dd"),
            checkOut = checkIn.AddDays(nightsOverride ?? 3).ToString("yyyy-MM-dd"),
            adults = 2,
            childAges = childAge is { } age ? new[] { age } : Array.Empty<int>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unavailable_supplier_is_a_503_and_no_availability_an_empty_result()
    {
        (await Send(HttpMethod.Post, _searches, body: Search(MockHotelScenarios.UnavailableDestination))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var none = await Send(HttpMethod.Post, _searches, body: Search(MockHotelScenarios.NoAvailabilityDestination));
        (await Read(none)).GetProperty("offers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Selecting_is_idempotent_per_caller_and_a_confirmed_price_is_the_searched_one()
    {
        var token = Token();
        var (searchId, offerId, price) = await FirstOffer("PAR");

        using var first = await Send(HttpMethod.Post, _selections, token, new { searchId, offerId });
        using var again = await Send(HttpMethod.Post, _selections, token, new { searchId, offerId });

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var selectionId = (await Read(first)).GetProperty("selectedOfferId").GetGuid();
        (await Read(again)).GetProperty("selectedOfferId").GetGuid().ShouldBe(selectionId);

        using var confirmed = await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations", token);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Read(confirmed)).GetProperty("totalPrice").GetProperty("amount").GetString().ShouldBe(price);
    }

    [Fact]
    public async Task A_changed_price_must_be_accepted_by_its_quote_before_it_counts()
    {
        var token = Token();
        var (searchId, offerId, price) = await FirstOffer(MockHotelScenarios.PriceChangedDestination);
        using var selected = await Send(HttpMethod.Post, _selections, token, new { searchId, offerId });
        var selectionId = (await Read(selected)).GetProperty("selectedOfferId").GetGuid();

        using var changed = await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations", token);
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await Read(changed);
        problem.GetProperty("type").GetString().ShouldBe("price-changed");
        problem.GetProperty("previousTotalPrice").GetProperty("amount").GetString().ShouldBe(price);
        var quote = problem.GetProperty("priceQuoteId").GetGuid();

        (await Send(HttpMethod.Post, $"{_selections}/{selectionId}/price-acceptances", token, new { priceQuoteId = Guid.NewGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // a stale quote
        using var accepted = await Send(HttpMethod.Post, $"{_selections}/{selectionId}/price-acceptances", token, new { priceQuoteId = quote });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Read(accepted)).GetProperty("totalPrice").GetProperty("amount").GetString()
            .ShouldBe(problem.GetProperty("newTotalPrice").GetProperty("amount").GetString());
    }

    [Theory]
    [InlineData(MockHotelScenarios.ExpiredDestination, "offer-expired")]
    [InlineData(MockHotelScenarios.SoldOutDestination, "sold-out")]
    public async Task An_expired_or_sold_out_room_ends_the_selection(string destination, string type)
    {
        var (searchId, offerId, _) = await FirstOffer(destination);
        using var selected = await Send(HttpMethod.Post, _selections, body: new { searchId, offerId });
        var selectionId = (await Read(selected)).GetProperty("selectedOfferId").GetGuid();

        using var ended = await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations");
        using var again = await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations");

        (await Read(ended)).GetProperty("type").GetString().ShouldBe(type);
        (await Read(again)).GetProperty("type").GetString().ShouldBe(type); // terminal: no new supplier call changes it
    }

    [Fact]
    public async Task Another_customers_selection_is_not_found_and_a_refused_token_is_never_anonymous()
    {
        var (searchId, offerId, _) = await FirstOffer("PAR");
        using var selected = await Send(HttpMethod.Post, _selections, Token(), new { searchId, offerId });
        var selectionId = (await Read(selected)).GetProperty("selectedOfferId").GetGuid();

        (await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations", Token())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{_selections}/{selectionId}/revalidations")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // anonymous
        (await Send(HttpMethod.Post, _selections, "not-a-token", new { searchId, offerId })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, _selections, body: new { searchId = Guid.NewGuid(), offerId })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);

    private static string Token() => TestCustomerTokens.For($"object-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private object Search(string destination) => new
    {
        destination,
        checkIn = Today.AddDays(40).ToString("yyyy-MM-dd"),
        checkOut = Today.AddDays(43).ToString("yyyy-MM-dd"),
        adults = 2,
        childAges = new[] { 8 },
    };

    private async Task<(Guid SearchId, Guid OfferId, string Price)> FirstOffer(string destination)
    {
        using var response = await Send(HttpMethod.Post, _searches, body: Search(destination));
        var search = await Read(response);
        var offer = search.GetProperty("offers")[0];
        return (search.GetProperty("searchId").GetGuid(), offer.GetProperty("offerId").GetGuid(), offer.GetProperty("totalPrice").GetProperty("amount").GetString()!);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token = null, object? body = null)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
