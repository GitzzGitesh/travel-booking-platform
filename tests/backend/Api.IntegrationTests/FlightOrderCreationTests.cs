using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Phase 3 chunk 1: orders are created from a persisted, revalidated flight selection through the composed Api host and
/// a real SQL Server. Orders reads the selection only through Flights.Contracts. No endpoint exists yet (Q8, ADR 0006).
/// </summary>
public sealed class FlightOrderCreationTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task A_confirmed_selection_is_ordered_at_its_agreed_price_with_a_persisted_timeline()
    {
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();

        var result = await Create(key, selection.Id);

        result.Value.Created.ShouldBeTrue();
        var stored = await Load(result.Value.Order.Id);
        stored.Status.ShouldBe(OrderStatus.AwaitingPayment);
        stored.IdempotencyKey.ShouldBe(key);
        var item = stored.Items.ShouldHaveSingleItem();
        item.SelectedOfferId.ShouldBe(selection.Id);
        item.AgreedPrice.ShouldBe(selection.AgreedPrice);
        stored.Timeline.Select(e => e.ToStatus).ShouldBe(["Draft", "AwaitingPayment"]);
        stored.CustomerId.ShouldBe(_customer);
        stored.Timeline.ShouldAllBe(e => e.Actor == $"customer:{_customer}" && e.CorrelationId == "test-trace");
    }

    [Fact]
    public async Task After_an_accepted_price_change_the_order_uses_the_accepted_price()
    {
        var selection = await ConfirmedSelection(MockRevalidationScenarios.PriceChangedDestination);

        var result = await Create(NewKey(), selection.Id);

        result.Value.Order.Total.ShouldBe(selection.AgreedPrice);
        selection.AgreedPrice.ShouldNotBe(selection.SelectedPrice);
        var item = (await Load(result.Value.Order.Id)).Items[0];
        item.AcceptedPriceQuoteId.ShouldNotBeNull();
        item.PriceAcceptedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Replaying_the_key_returns_the_same_order_and_reusing_it_for_another_selection_is_refused()
    {
        var selection = await ConfirmedSelection("JFK");
        var other = await ConfirmedSelection("JFK");
        var key = NewKey();

        var first = await Create(key, selection.Id);
        var replay = await Create(key, selection.Id);
        var reused = await Create(key, other.Id);

        replay.Value.Created.ShouldBeFalse();
        replay.Value.Order.Id.ShouldBe(first.Value.Order.Id);
        reused.Error.ShouldBeOfType<CreateFlightOrderFailure.IdempotencyKeyReused>();
    }

    [Fact]
    public async Task Parallel_requests_with_one_key_create_exactly_one_order()
    {
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Create(key, selection.Id)));

        results.Where(r => !r.IsSuccess).Select(r => r.Error.ToString()).ShouldBeEmpty();
        results.Count(r => r.Value.Created).ShouldBe(1);
        results.Select(r => r.Value.Order.Id).Distinct().ShouldHaveSingleItem();
        (await CountOrdersFor(selection.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_requests_with_different_keys_for_one_selection_create_exactly_one_order()
    {
        var selection = await ConfirmedSelection("JFK");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Create(NewKey(), selection.Id)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error is CreateFlightOrderFailure.SelectionAlreadyOrdered);
        (await CountOrdersFor(selection.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Customers_are_isolated_one_cannot_load_or_learn_anothers_order()
    {
        var selection = await ConfirmedSelection("JFK");
        var key = NewKey();
        var mine = (await Create(key, selection.Id)).Value.Order;

        var theirs = await Create(NewKey(), selection.Id, customer: "test-customer-2");
        var sameKey = await Create(key, (await ConfirmedSelection("JFK")).Id, customer: "test-customer-2");

        theirs.Error.ShouldBe(new CreateFlightOrderFailure.SelectionAlreadyOrdered(null));
        sameKey.Value.Created.ShouldBeTrue(); // keys are per customer
        using var scope = api.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderStore>();
        (await store.FindOwnedAsync(mine.Id, "test-customer-2", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.FindOwnedAsync(mine.Id, _customer, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_selection_that_was_not_revalidated_cannot_be_ordered()
    {
        var selected = await Select("JFK");

        var result = await Create(NewKey(), selected);

        result.Error.ShouldBe(new CreateFlightOrderFailure.SelectionUnavailable(FlightSelectionUnavailable.NeedsPriceCheck));
    }

    [Theory]
    [InlineData(MockRevalidationScenarios.SoldOutDestination, FlightSelectionUnavailable.SoldOut)]
    [InlineData(MockRevalidationScenarios.OfferExpiredDestination, FlightSelectionUnavailable.Expired)]
    public async Task Sold_out_or_expired_selections_cannot_be_ordered(string destination, FlightSelectionUnavailable reason)
    {
        var selected = await Select(destination);
        using var client = api.CreateClient();
        (await client.PostAsync($"/api/v1/flights/selected-offers/{selected}/revalidations", null, TestContext.Current.CancellationToken)).Dispose();

        var result = await Create(NewKey(), selected);

        result.Error.ShouldBe(new CreateFlightOrderFailure.SelectionUnavailable(reason));
    }

    [Fact]
    public async Task Item_transitions_persist_and_only_append_to_the_timeline()
    {
        var selection = await ConfirmedSelection("JFK");
        var created = (await Create(NewKey(), selection.Id)).Value.Order;
        var itemId = created.Items[0].Id;

        await Change(created.Id, order => order.StartBooking("auth-test-1", Context()).IsSuccess);
        await Change(created.Id, order => order.AwaitConfirmation(itemId, "Supplier timeout", Context()).IsSuccess);
        await Change(created.Id, order => order.Confirm(itemId, "mock", "ABC234", Context()).IsSuccess);

        var stored = await Load(created.Id);
        stored.Status.ShouldBe(OrderStatus.Confirmed);
        stored.PaymentAuthorizationId.ShouldBe("auth-test-1");
        stored.Items[0].SupplierLocator.ShouldBe("ABC234");
        stored.Timeline.Select(e => e.ToStatus).ShouldBe(["Draft", "AwaitingPayment", "Booking", "PendingConfirmation", "Confirmed"]);
        stored.Timeline.Take(2).Select(e => e.Id).ShouldBe(created.Timeline.Select(e => e.Id)); // earlier entries untouched
        stored.Timeline.Select(e => e.ProviderReference).ShouldBe([null, null, "auth-test-1", null, "mock:ABC234"]);
    }

    [Fact]
    public async Task Concurrent_changes_to_one_order_cannot_both_be_saved()
    {
        var selection = await ConfirmedSelection("JFK");
        var created = (await Create(NewKey(), selection.Id)).Value.Order;
        var itemId = created.Items[0].Id;

        using var first = api.Services.CreateScope();
        using var second = api.Services.CreateScope();
        var firstStore = first.ServiceProvider.GetRequiredService<IOrderStore>();
        var secondStore = second.ServiceProvider.GetRequiredService<IOrderStore>();
        (await firstStore.FindAsync(created.Id, TestContext.Current.CancellationToken))!.StartBooking("auth-a", Context());
        (await secondStore.FindAsync(created.Id, TestContext.Current.CancellationToken))!.Abandon(itemId, "Authorization failed", Context());

        (await firstStore.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await secondStore.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await Load(created.Id)).Items[0].Status.ShouldBe(FlightOrderItemStatus.Booking);
    }

    private TransitionContext Context() => new(api.Clock.GetUtcNow(), "system", "test-trace");

    private const string _customer = "test-customer-1";

    private async Task<TravelBooking.BuildingBlocks.Result<CreatedOrder, CreateFlightOrderFailure>> Create(string key, Guid selectedOfferId, string? customer = null)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreateFlightOrderHandler>()
            .HandleAsync(new CreateFlightOrder(customer ?? _customer, key, selectedOfferId, "test-trace"), TestContext.Current.CancellationToken);
    }

    private async Task Change(Guid orderId, Func<Order, bool> transition)
    {
        using var scope = api.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderStore>();
        var order = (await store.FindAsync(orderId, TestContext.Current.CancellationToken))!;
        transition(order).ShouldBeTrue();
        (await store.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private async Task<Order> Load(Guid orderId)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IOrderStore>().FindAsync(orderId, TestContext.Current.CancellationToken))!;
    }

    private async Task<int> CountOrdersFor(Guid selectedOfferId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders
            .CountAsync(o => o.Items.Any(i => i.SelectedOfferId == selectedOfferId), TestContext.Current.CancellationToken);
    }

    private static string NewKey() => $"order-{Guid.NewGuid():N}";

    /// <summary>Search, select and revalidate over HTTP; accept a changed price. Returns the selection and its prices.</summary>
    private async Task<(Guid Id, TravelBooking.BuildingBlocks.Money SelectedPrice, TravelBooking.BuildingBlocks.Money AgreedPrice)> ConfirmedSelection(string destination)
    {
        var selected = await Select(destination);
        using var client = api.CreateClient();
        using var revalidated = await client.PostAsync($"/api/v1/flights/selected-offers/{selected}/revalidations", null, TestContext.Current.CancellationToken);
        if (revalidated.StatusCode == (HttpStatusCode)422)
        {
            var problem = (await revalidated.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!;
            var quote = problem.Extensions["priceQuoteId"].ShouldBeOfType<JsonElement>().GetGuid();
            using var accepted = await client.PostAsJsonAsync($"/api/v1/flights/selected-offers/{selected}/price-acceptances", new { priceQuoteId = quote }, TestContext.Current.CancellationToken);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var scope = api.Services.CreateScope();
        var offer = await scope.ServiceProvider.GetRequiredService<TravelBooking.Modules.Flights.Infrastructure.FlightsDbContext>()
            .SelectedOffers.AsNoTracking().SingleAsync(o => o.Id == selected, TestContext.Current.CancellationToken);
        return (selected, offer.TotalPrice, offer.AgreedPrice);
    }

    private async Task<Guid> Select(string destination)
    {
        using var client = api.CreateClient();
        var departure = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime).AddDays(30).ToString("yyyy-MM-dd");
        using var search = await client.PostAsJsonAsync("/api/v1/flights/searches", new { origin = "LHR", destination, departureDate = departure }, TestContext.Current.CancellationToken);
        var found = (await search.Content.ReadFromJsonAsync<SearchResult>(JsonSerializerOptions.Web, TestContext.Current.CancellationToken))!;
        using var select = await client.PostAsJsonAsync("/api/v1/flights/selected-offers", new { found.SearchId, found.Offers[0].OfferId }, TestContext.Current.CancellationToken);
        select.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await select.Content.ReadFromJsonAsync<Selection>(JsonSerializerOptions.Web, TestContext.Current.CancellationToken))!.SelectedOfferId;
    }

    private sealed record Offer(Guid OfferId);

    private sealed record SearchResult(Guid SearchId, IReadOnlyList<Offer> Offers);

    private sealed record Selection(Guid SelectedOfferId);
}
