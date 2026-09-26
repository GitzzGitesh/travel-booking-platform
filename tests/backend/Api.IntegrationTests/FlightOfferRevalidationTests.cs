using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TravelBooking.Integrations.Flights.Mock;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Revalidating a selected offer before any booking step (Phase 2, chunk 5): F-01 price changed (a quote the customer
/// accepts by id), F-02 offer expired and F-03 sold out (search again). The mock picks the scenario by destination.
/// </summary>
public sealed class FlightOfferRevalidationTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task An_unchanged_price_is_confirmed_and_revalidation_can_be_repeated()
    {
        var selected = await Select("JFK");

        using var first = await Revalidate(selected.SelectedOfferId);
        using var again = await Revalidate(selected.SelectedOfferId);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirmed = await Read<Confirmed>(first);
        confirmed.SelectedOfferId.ShouldBe(selected.SelectedOfferId);
        Same(confirmed.TotalPrice, selected.TotalPrice);
        Same(confirmed.SelectedTotalPrice, selected.TotalPrice);
        confirmed.OfferExpiresAt.ShouldBeGreaterThan(api.Clock.GetUtcNow());
    }

    [Fact]
    public async Task F01_a_changed_price_needs_the_customers_acceptance_and_is_never_applied_silently()
    {
        var selected = await Select(MockRevalidationScenarios.PriceChangedDestination);

        using var response = await Revalidate(selected.SelectedOfferId);

        var problem = await Problem(response, 422, "price-changed");
        var previous = Money(problem, "previousTotalPrice");
        var changed = Money(problem, "newTotalPrice");
        Same(previous, selected.TotalPrice);
        // The mock rescales one adult's base fare and taxes by 15%, each to the cent: within a cent of the whole total's.
        var expected = decimal.Round(decimal.Parse(selected.TotalPrice.Amount, System.Globalization.CultureInfo.InvariantCulture) * 1.15m, 2);
        decimal.Parse(changed.Amount, System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(expected - 0.01m, expected + 0.01m);
        problem.Extensions["requiresConfirmation"].ShouldBeOfType<JsonElement>().GetBoolean().ShouldBeTrue();
        var quote = problem.Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();

        // The stored selection still carries the price the customer chose.
        using var reselect = await Post(Urls.Select, new { selected.SearchId, selected.OfferId });
        Same((await Read<Selection>(reselect)).TotalPrice, selected.TotalPrice);

        using var stale = await Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = Guid.NewGuid() });
        await Problem(stale, 409, "price-quote-stale");

        using var accepted = await Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = quote });
        using var replay = await Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = quote });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirmed = await Read<Confirmed>(accepted);
        Same(confirmed.TotalPrice, changed);
        Same(confirmed.SelectedTotalPrice, selected.TotalPrice);

        // The supplier keeps quoting the same higher price, which now matches what the customer accepted.
        using var afterAcceptance = await Revalidate(selected.SelectedOfferId);
        afterAcceptance.StatusCode.ShouldBe(HttpStatusCode.OK);
        Same((await Read<Confirmed>(afterAcceptance)).TotalPrice, changed);
    }

    [Fact]
    public async Task F04_a_price_sent_with_the_acceptance_is_ignored_and_the_stored_quote_applies()
    {
        var selected = await Select(MockRevalidationScenarios.PriceChangedDestination);
        using var response = await Revalidate(selected.SelectedOfferId);
        var problem = await Problem(response, 422, "price-changed");
        var quote = problem.Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();

        using var accepted = await Post(Urls.Accept(selected.SelectedOfferId), new
        {
            priceQuoteId = quote,
            totalPrice = new { amount = "1.00", currency = "XTS" },
            newTotalPrice = new { amount = "1.00", currency = "XTS" },
        });

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        Same((await Read<Confirmed>(accepted)).TotalPrice, Money(problem, "newTotalPrice"));
    }

    [Fact]
    public async Task F01_parallel_acceptances_of_one_quote_confirm_it_once()
    {
        var selected = await Select(MockRevalidationScenarios.PriceChangedDestination);
        using var response = await Revalidate(selected.SelectedOfferId);
        var quote = (await Problem(response, 422, "price-changed")).Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = quote })));

        // Each request either applied the quote, replayed it, or lost the race and may retry: never a second effect.
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.ShouldContain(r => r.StatusCode == HttpStatusCode.OK);
        using var retry = await Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = quote });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    [Theory]
    [InlineData(MockRevalidationScenarios.OfferExpiredDestination, "offer-expired")]
    [InlineData(MockRevalidationScenarios.SoldOutDestination, "sold-out")]
    public async Task F02_and_F03_ask_the_customer_to_search_again_and_stay_unavailable(string destination, string type)
    {
        var selected = await Select(destination);

        using var first = await Revalidate(selected.SelectedOfferId);
        using var again = await Revalidate(selected.SelectedOfferId);
        using var accept = await Post(Urls.Accept(selected.SelectedOfferId), new { priceQuoteId = Guid.NewGuid() });

        await Problem(first, 422, type);
        await Problem(again, 422, type);
        await Problem(accept, 422, type);

        // Searching again still works.
        (await Search("JFK")).Offers.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task F02_an_offer_past_its_expiry_is_expired()
    {
        var selected = await Select("JFK");
        api.Clock.Advance(TimeSpan.FromMinutes(31)); // mock offers expire 30 minutes after the search

        using var response = await Revalidate(selected.SelectedOfferId);

        await Problem(response, 422, "offer-expired");
    }

    [Fact]
    public async Task An_unknown_selection_is_not_found()
    {
        using var revalidate = await Revalidate(Guid.NewGuid());
        using var accept = await Post(Urls.Accept(Guid.NewGuid()), new { priceQuoteId = Guid.NewGuid() });

        await Problem(revalidate, 404, "selected-offer-not-found");
        await Problem(accept, 404, "selected-offer-not-found");
    }

    [Fact]
    public async Task Accepting_without_a_quote_id_is_a_validation_error()
    {
        var selected = await Select("JFK");

        using var response = await Post(Urls.Accept(selected.SelectedOfferId), new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Responses_never_expose_the_suppliers_offer_token()
    {
        var selected = await Select(MockRevalidationScenarios.PriceChangedDestination);

        using var response = await Revalidate(selected.SelectedOfferId);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("mock-", Case.Insensitive);
    }

    private async Task<Selection> Select(string destination)
    {
        var search = await Search(destination);
        using var response = await Post(Urls.Select, new { search.SearchId, search.Offers[0].OfferId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await Read<Selection>(response);
    }

    private async Task<SearchResult> Search(string destination)
    {
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var response = await Post(Urls.Search, new { origin = "LHR", destination, departureDate = departure, adults = 2 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await Read<SearchResult>(response);
    }

    private Task<HttpResponseMessage> Revalidate(Guid selectedOfferId) => Post(Urls.Revalidate(selectedOfferId), null);

    private async Task<HttpResponseMessage> Post(string url, object? body)
    {
        using var client = api.CreateClient();
        return body is null
            ? await client.PostAsync(url, null, TestContext.Current.CancellationToken)
            : await client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
    }

    private static async Task<ProblemDetails> Problem(HttpResponseMessage response, int status, string type)
    {
        ((int)response.StatusCode).ShouldBe(status, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!;
        problem.Type.ShouldBe(type);
        problem.Extensions.ShouldContainKey("traceId");
        return problem;
    }

    // Amounts read back from decimal(19,4) carry its scale ("540.0000"): compare values, not strings.
    private static void Same(MoneyAmount actual, MoneyAmount expected)
    {
        actual.Currency.ShouldBe(expected.Currency);
        Value(actual).ShouldBe(Value(expected));
    }

    private static decimal Value(MoneyAmount money) => decimal.Parse(money.Amount, System.Globalization.CultureInfo.InvariantCulture);

    private static MoneyAmount Money(ProblemDetails problem, string name) =>
        problem.Extensions[name].ShouldBeOfType<JsonElement>().Deserialize<MoneyAmount>(JsonSerializerOptions.Web)!;

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, TestContext.Current.CancellationToken))!;

    private static class Urls
    {
        public const string Search = "/api/v1/flights/searches";
        public const string Select = "/api/v1/flights/selected-offers";

        public static string Revalidate(Guid id) => $"{Select}/{id}/revalidations";

        public static string Accept(Guid id) => $"{Select}/{id}/price-acceptances";
    }

    private sealed record MoneyAmount(string Amount, string Currency);

    private sealed record Offer(Guid OfferId, MoneyAmount TotalPrice);

    private sealed record SearchResult(Guid SearchId, IReadOnlyList<Offer> Offers);

    private sealed record Selection(Guid SelectedOfferId, Guid SearchId, Guid OfferId, MoneyAmount TotalPrice);

    private sealed record Confirmed(Guid SelectedOfferId, MoneyAmount TotalPrice, MoneyAmount SelectedTotalPrice, DateTimeOffset OfferExpiresAt);
}
