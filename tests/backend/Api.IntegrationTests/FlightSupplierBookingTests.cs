using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Infrastructure;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Supplier booking through the composed Api host (Phase 2, chunk 6): a selection is searched, selected and revalidated
/// over HTTP, then its persisted snapshot is booked with the host's supplier booking component and mock provider.
/// There is no booking endpoint yet: bookings are made for Order items in Phase 3 (ADR 0005).
/// </summary>
public sealed class FlightSupplierBookingTests(SqlApiFactory api) : IClassFixture<SqlApiFactory>
{
    [Fact]
    public async Task A_confirmed_selection_books_at_its_agreed_price_and_is_found_by_our_reference()
    {
        var offer = await ConfirmedSelection("JFK");
        var reference = NewReference();

        var outcome = await Book(Request(offer, reference, "Traveller"));
        var recovered = await Reconcile(reference, offer.AgreedPrice);

        var booked = outcome.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation;
        booked.TotalPrice.ShouldBe(offer.AgreedPrice);
        booked.Booking.ProviderId.ShouldBe("mock");
        recovered.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.Booking.ShouldBe(booked.Booking);
    }

    [Fact]
    public async Task Booking_the_same_reference_again_returns_the_same_booking()
    {
        var offer = await ConfirmedSelection("JFK");
        var reference = NewReference();

        var first = await Book(Request(offer, reference, "Traveller"));
        var again = await Book(Request(offer, reference, "Traveller"));

        again.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.Booking
            .ShouldBe(first.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.Booking);
    }

    [Fact]
    public async Task A_rejected_booking_is_definitive_and_nothing_is_found()
    {
        var offer = await ConfirmedSelection("JFK");
        var reference = NewReference();

        var outcome = await Book(Request(offer, reference, MockBookingScenarios.RejectedFamilyName));

        outcome.ShouldBeOfType<SupplierBookingOutcome.NotBooked>().Reason.ShouldBe(SupplierBookingFailureReason.Rejected);
        (await Reconcile(reference, offer.AgreedPrice)).ShouldBeOfType<SupplierBookingOutcome.NotFound>();
    }

    [Fact]
    public async Task A_timeout_after_booking_is_unknown_then_recovered_as_booked_by_our_reference()
    {
        var offer = await ConfirmedSelection("JFK");
        var reference = NewReference();

        var outcome = await Book(Request(offer, reference, MockBookingScenarios.TimeoutBookedFamilyName));
        var recovered = await Reconcile(reference, offer.AgreedPrice);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Unknown>();
        recovered.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.ClientReference.ShouldBe(reference);
    }

    [Fact]
    public async Task A_timeout_without_booking_is_unknown_then_resolved_as_not_found()
    {
        var offer = await ConfirmedSelection("JFK");
        var reference = NewReference();

        var outcome = await Book(Request(offer, reference, MockBookingScenarios.TimeoutNotBookedFamilyName));
        var recovered = await Reconcile(reference, offer.AgreedPrice);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Unknown>();
        recovered.ShouldBeOfType<SupplierBookingOutcome.NotFound>();
    }

    [Fact]
    public async Task After_an_accepted_price_change_only_the_agreed_price_books()
    {
        var offer = await ConfirmedSelection(MockRevalidationScenarios.PriceChangedDestination);
        offer.AgreedPrice.ShouldNotBe(offer.TotalPrice);

        var atSelectedPrice = await Book(Request(offer, NewReference(), "Traveller") with { ExpectedTotalPrice = offer.TotalPrice });
        var atAgreedPrice = await Book(Request(offer, NewReference(), "Traveller"));

        atSelectedPrice.ShouldBeOfType<SupplierBookingOutcome.NotBooked>().Reason.ShouldBe(SupplierBookingFailureReason.PriceChanged);
        atAgreedPrice.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.TotalPrice.ShouldBe(offer.AgreedPrice);
    }

    [Theory]
    [InlineData(MockRevalidationScenarios.SoldOutDestination)]
    [InlineData(MockRevalidationScenarios.OfferExpiredDestination)]
    public async Task Offers_that_can_no_longer_be_bought_are_not_booked(string destination)
    {
        var selected = await Select(destination);
        var offer = await Load(selected);

        var outcome = await Book(Request(offer, NewReference(), "Traveller"));

        outcome.ShouldBeOfType<SupplierBookingOutcome.NotBooked>();
    }

    /// <summary>Search, select and revalidate over HTTP; for a price change, accept the quote. Returns the stored snapshot.</summary>
    private async Task<SelectedOffer> ConfirmedSelection(string destination)
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
        else
        {
            revalidated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var offer = await Load(selected);
        offer.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        return offer;
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

    private async Task<SelectedOffer> Load(Guid selectedOfferId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlightsDbContext>().SelectedOffers.AsNoTracking()
            .SingleAsync(o => o.Id == selectedOfferId, TestContext.Current.CancellationToken);
    }

    // One adult, as searched. Synthetic travellers only (testing rules).
    private static FlightBookingDetails Request(SelectedOffer offer, ClientReference reference, string familyName) =>
        new(reference, new ProviderOfferRef(offer.ProviderId, offer.ProviderOfferToken), offer.AgreedPrice, [new FlightPassenger(PassengerType.Adult, "Test", familyName)]);

    private async Task<SupplierBookingOutcome> Book(FlightBookingDetails request)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlightSupplierBooking>().BookAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<SupplierBookingOutcome> Reconcile(ClientReference reference, TravelBooking.BuildingBlocks.Money expectedTotalPrice)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlightSupplierBooking>().ReconcileAsync("mock", reference, expectedTotalPrice, TestContext.Current.CancellationToken);
    }

    private static ClientReference NewReference() => new($"item-{Guid.NewGuid():N}");

    private sealed record Offer(Guid OfferId);

    private sealed record SearchResult(Guid SearchId, IReadOnlyList<Offer> Offers);

    private sealed record Selection(Guid SelectedOfferId);
}
