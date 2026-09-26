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
    public async Task An_enriched_selection_keeps_its_fare_facts_and_leg_details()
    {
        var criteria = new FlightSearchCriteria(new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(2, 0, 1), CabinClass.Economy);
        var fare = new FlightFare
        {
            PriceBreakdown = new FlightPriceBreakdown(
            [
                new PassengerFare(PassengerType.Adult, 2, Xts(100.1234m), Xts(17.66m)),
                new PassengerFare(PassengerType.Infant, 1, Xts(10.01m), Xts(1.77m)),
            ]),
            ValidatingCarrier = "ZZ",
            Baggage = new BaggageAllowance(1, 1, 23),
            Conditions = new FareConditions(FareAllowance.NotAllowed, FareAllowance.AllowedWithFee),
            TicketingDeadline = _now.AddDays(1),
        };
        var offer = new FlightOffer(
            new ProviderOfferRef("mock", "token"),
            fare.PriceBreakdown.Total,
            _now.AddMinutes(30),
            [new FlightSlice([new FlightSegment("ZZ", "ZZ123", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))
            {
                OperatingCarrier = "ZY",
                Duration = TimeSpan.FromMinutes(495),
                FareBasis = "M1MOCK",
            }])])
        {
            Fare = fare,
        };
        var selection = SelectedOffer.Select(Guid.NewGuid(), Guid.NewGuid(), criteria, offer, _now);
        await using (var write = sql.CreateContext())
        {
            (await new SqlSelectedOfferStore(write).TryAddAsync(selection, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        await using var read = sql.CreateContext();
        var stored = (await new SqlSelectedOfferStore(read).FindByIdAsync(selection.Id, TestContext.Current.CancellationToken))!;

        stored.Fare.PriceBreakdown!.Total.ShouldBe(Xts(247.3468m)); // decimals kept exactly
        stored.Fare.PriceBreakdown.Passengers.Select(p => (p.Type, p.Count)).ShouldBe([(PassengerType.Adult, 2), (PassengerType.Infant, 1)]);
        (stored.Fare.ValidatingCarrier, stored.Fare.Baggage, stored.Fare.Conditions, stored.Fare.TicketingDeadline)
            .ShouldBe(("ZZ", fare.Baggage, fare.Conditions, _now.AddDays(1)));
        var segment = stored.Slices.Single().Segments.Single();
        (segment.OperatingCarrier, segment.Duration, segment.FareBasis).ShouldBe(("ZY", TimeSpan.FromMinutes(495), "M1MOCK"));
    }

    [Fact]
    public async Task A_selection_stored_before_fare_facts_were_kept_reads_as_not_stated()
    {
        var selection = NewSelection();
        await using (var write = sql.CreateContext())
        {
            (await new SqlSelectedOfferStore(write).TryAddAsync(selection, TestContext.Current.CancellationToken)).ShouldBeTrue();
            await write.Database.ExecuteSqlAsync($"UPDATE flights.SelectedOffers SET FareJson = NULL WHERE Id = {selection.Id}", TestContext.Current.CancellationToken);
        }

        await using var read = sql.CreateContext();
        (await new SqlSelectedOfferStore(read).FindByIdAsync(selection.Id, TestContext.Current.CancellationToken))!.Fare.ShouldBe(FlightFare.NotStated);
    }

    private static Money Xts(decimal amount) => new(amount, new CurrencyCode("XTS"));

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

    [Fact]
    public async Task Revalidation_state_and_quoted_prices_round_trip()
    {
        var offer = NewSelection();
        await using (var write = sql.CreateContext())
        {
            await new SqlSelectedOfferStore(write).TryAddAsync(offer, TestContext.Current.CancellationToken);
        }

        await using (var update = sql.CreateContext())
        {
            var store = new SqlSelectedOfferStore(update);
            var tracked = (await store.FindForUpdateAsync(offer.Id, TestContext.Current.CancellationToken))!;
            tracked.Revalidate(Offer(1419.7m), _now);
            (await store.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        await using var read = sql.CreateContext();
        var stored = (await new SqlSelectedOfferStore(read).FindForUpdateAsync(offer.Id, TestContext.Current.CancellationToken))!;
        stored.Status.ShouldBe(SelectedOfferStatus.PriceChanged);
        stored.QuotedPrice.ShouldBe(new Money(1419.7m, new CurrencyCode("XTS")));
        stored.TotalPrice.ShouldBe(new Money(1234.5678m, new CurrencyCode("XTS")));
        stored.ConfirmedPrice.ShouldBeNull();
        stored.RevalidatedAt.ShouldBe(_now);

        stored.AcceptPrice(stored.PriceQuoteId!.Value, _now).IsSuccess.ShouldBeTrue();
        (await new SqlSelectedOfferStore(read).TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        await using var check = sql.CreateContext();
        var confirmed = (await new SqlSelectedOfferStore(check).FindForUpdateAsync(offer.Id, TestContext.Current.CancellationToken))!;
        confirmed.Status.ShouldBe(SelectedOfferStatus.Confirmed);
        confirmed.ConfirmedPrice.ShouldBe(new Money(1419.7m, new CurrencyCode("XTS")));
        confirmed.QuotedPrice.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_changes_to_one_selection_cannot_both_be_saved()
    {
        var offer = NewSelection();
        await using (var write = sql.CreateContext())
        {
            await new SqlSelectedOfferStore(write).TryAddAsync(offer, TestContext.Current.CancellationToken);
        }

        await using var first = sql.CreateContext();
        await using var second = sql.CreateContext();
        var firstStore = new SqlSelectedOfferStore(first);
        var secondStore = new SqlSelectedOfferStore(second);
        (await firstStore.FindForUpdateAsync(offer.Id, TestContext.Current.CancellationToken))!.Revalidate(Offer(1300m), _now);
        (await secondStore.FindForUpdateAsync(offer.Id, TestContext.Current.CancellationToken))!.MarkUnavailable(SelectedOfferStatus.SoldOut);

        (await firstStore.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await secondStore.TrySaveAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_database_rejects_an_unknown_status()
    {
        var offer = NewSelection();
        await using var db = sql.CreateContext();
        db.SelectedOffers.Add(offer);
        db.Entry(offer).Property(o => o.Status).CurrentValue = (SelectedOfferStatus)42;

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static FlightOffer Offer(decimal amount) => new(
        new ProviderOfferRef("mock", "mock-0-LHRJFK-20270214--Business-2A1C1I"),
        new Money(amount, new CurrencyCode("XTS")),
        _now.AddMinutes(30),
        [new FlightSlice([new FlightSegment("ZZ", "ZZ123", new AirportCode("LHR"), new AirportCode("JFK"), new DateTime(2027, 2, 14, 7, 5, 0), new DateTime(2027, 2, 14, 9, 20, 0))])]);

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
