using Microsoft.EntityFrameworkCore;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Infrastructure;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.IntegrationTests;

public sealed class SelectedOfferPersistenceTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migrations_create_the_flights_schema_with_no_pending_changes()
    {
        await using var db = sql.CreateContext();

        (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse("the model has changes that are not in a migration");
    }

    [Fact]
    public async Task A_snapshot_round_trips_exactly()
    {
        var offer = NewSelection();
        await using (var write = sql.CreateContext())
        {
            (await new SqlSelectedOfferStore(write).TryAddAsync(offer, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        await using var read = sql.CreateContext();
        var stored = await new SqlSelectedOfferStore(read).FindAsync(offer.SearchId, offer.OfferId, TestContext.Current.CancellationToken);

        stored.ShouldNotBeNull();
        stored.TotalPrice.ShouldBe(new Money(1234.5678m, new CurrencyCode("XTS")));
        stored.ProviderOfferToken.ShouldBe(offer.ProviderOfferToken);
        stored.OfferExpiresAt.ShouldBe(offer.OfferExpiresAt);
        stored.Cabin.ShouldBe(CabinClass.Business);
        (stored.Adults, stored.Children, stored.Infants).ShouldBe((2, 1, 1));
        var segment = stored.Slices.ShouldHaveSingleItem().Segments.ShouldHaveSingleItem();
        segment.Origin.ShouldBe(new AirportCode("LHR"));
        segment.DepartureLocal.ShouldBe(new DateTime(2027, 2, 14, 7, 5, 0, DateTimeKind.Unspecified));
        segment.DepartureLocal.Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    [Fact]
    public async Task The_database_allows_one_selection_per_offer_of_a_search()
    {
        var first = NewSelection();
        var duplicate = NewSelection(first.SearchId, first.OfferId);
        await using var db = sql.CreateContext();
        var store = new SqlSelectedOfferStore(db);

        (await store.TryAddAsync(first, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await store.TryAddAsync(duplicate, TestContext.Current.CancellationToken)).ShouldBeFalse();

        (await db.SelectedOffers.CountAsync(o => o.SearchId == first.SearchId, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_duplicate_selections_store_exactly_one_row()
    {
        var searchId = Guid.NewGuid();
        var offerId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = sql.CreateContext();
            return await new SqlSelectedOfferStore(db).TryAddAsync(NewSelection(searchId, offerId), TestContext.Current.CancellationToken);
        }));

        results.Count(added => added).ShouldBe(1);
        await using var check = sql.CreateContext();
        (await check.SelectedOffers.CountAsync(o => o.SearchId == searchId, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private static SelectedOffer NewSelection(Guid? searchId = null, Guid? offerId = null)
    {
        var criteria = new FlightSearchCriteria(
            new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(2, 1, 1), CabinClass.Business);
        var offer = new FlightOffer(
            new ProviderOfferRef("mock", "mock-0-LHRJFK-20270214--Business-2A1C1I"),
            new Money(1234.5678m, new CurrencyCode("XTS")),
            _now.AddMinutes(30),
            [new FlightSlice([new FlightSegment("ZZ", "ZZ123", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))])]);
        return SelectedOffer.Select(searchId ?? Guid.NewGuid(), offerId ?? Guid.NewGuid(), criteria, offer, _now);
    }
}
