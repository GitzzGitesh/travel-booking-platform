using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using TravelBooking.Modules.Flights.Infrastructure;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>The Api with a real SQL Server (Testcontainers), the Flights migrations applied, and a controllable clock.</summary>
public sealed class SqlApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlightsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
    }

    internal async Task<int> CountSelections(Guid searchId)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlightsDbContext>().SelectedOffers.CountAsync(o => o.SearchId == searchId);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Flights", _sql.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Orders", _sql.GetConnectionString());
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

/// <summary>
/// Selecting an offer (Phase 2, chunk 4; Option 2): search results are held in HybridCache under a searchId, and only
/// the selected offer is persisted, idempotently. Expired or unknown searches are F-02.
/// </summary>
public sealed class FlightOfferSelectionTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    private const string _searchUrl = "/api/v1/flights/searches";
    private const string _selectUrl = "/api/v1/flights/selected-offers";

    [Fact]
    public async Task Search_returns_a_search_id_and_unique_offer_ids()
    {
        var search = await Search();

        search.SearchId.ShouldNotBe(Guid.Empty);
        search.Offers.Select(o => o.OfferId).ShouldBeUnique();
        search.Offers.ShouldAllBe(o => o.OfferId != Guid.Empty);
    }

    [Fact]
    public async Task Selecting_an_offer_persists_only_that_offer_as_a_supplier_neutral_snapshot()
    {
        var search = await Search();
        var chosen = search.Offers[1];
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(_selectUrl, new { search.SearchId, chosen.OfferId }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var selected = System.Text.Json.JsonSerializer.Deserialize<SelectedOffer>(body, System.Text.Json.JsonSerializerOptions.Web)!;
        selected.OfferId.ShouldBe(chosen.OfferId);
        selected.TotalPrice.ShouldBe(chosen.TotalPrice);
        body.ShouldNotContain("mock-", Case.Insensitive); // the adapter's offer token is never exposed
        (await api.CountSelections(search.SearchId)).ShouldBe(1);
    }

    [Fact]
    public async Task Selecting_the_same_offer_again_returns_the_stored_selection()
    {
        var search = await Search();
        using var client = api.CreateClient();

        using var first = await client.PostAsJsonAsync(_selectUrl, new { search.SearchId, search.Offers[0].OfferId }, TestContext.Current.CancellationToken);
        using var replay = await client.PostAsJsonAsync(_selectUrl, new { search.SearchId, search.Offers[0].OfferId }, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Read<SelectedOffer>(replay)).SelectedOfferId.ShouldBe((await Read<SelectedOffer>(first)).SelectedOfferId);
    }

    [Fact]
    public async Task Parallel_duplicate_selections_create_exactly_one_snapshot()
    {
        var search = await Search();
        using var client = api.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            client.PostAsJsonAsync(_selectUrl, new { search.SearchId, search.Offers[2].OfferId }, TestContext.Current.CancellationToken)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(5);
        var ids = await Task.WhenAll(responses.Select(async r => (await Read<SelectedOffer>(r)).SelectedOfferId));
        ids.Distinct().ShouldHaveSingleItem();
        (await api.CountSelections(search.SearchId)).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task An_unknown_search_is_F02_offer_expired()
    {
        await ExpectOfferExpired(Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public async Task An_offer_that_is_not_in_the_search_is_F02_offer_expired()
    {
        var search = await Search();

        await ExpectOfferExpired(search.SearchId, Guid.NewGuid());
    }

    [Fact]
    public async Task An_expired_offer_is_F02_offer_expired_and_nothing_is_stored()
    {
        var search = await Search();
        api.Clock.Advance(TimeSpan.FromMinutes(31)); // mock offers expire 30 minutes after the search

        await ExpectOfferExpired(search.SearchId, search.Offers[0].OfferId);
        (await api.CountSelections(search.SearchId)).ShouldBe(0);
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "searchId": "00000000-0000-0000-0000-000000000001" }""")]
    [InlineData("""{ "searchId": "not-a-guid", "offerId": "00000000-0000-0000-0000-000000000001" }""")]
    public async Task Incomplete_selections_are_rejected(string json)
    {
        using var client = api.CreateClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(_selectUrl, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task ExpectOfferExpired(Guid searchId, Guid offerId)
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync(_selectUrl, new { searchId, offerId }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe((HttpStatusCode)422);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem!.Type.ShouldBe("offer-expired");
        problem.Extensions.ShouldContainKey("traceId");
    }

    private async Task<SearchResult> Search()
    {
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var response = await client.PostAsJsonAsync(_searchUrl, new { origin = "LHR", destination = "JFK", departureDate = departure }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await Read<SearchResult>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(System.Text.Json.JsonSerializerOptions.Web, TestContext.Current.CancellationToken))!;

    private sealed record Money(string Amount, string Currency);

    private sealed record Offer(Guid OfferId, Money TotalPrice);

    private sealed record SearchResult(Guid SearchId, IReadOnlyList<Offer> Offers);

    private sealed record SelectedOffer(Guid SelectedOfferId, Guid SearchId, Guid OfferId, Money TotalPrice);
}
