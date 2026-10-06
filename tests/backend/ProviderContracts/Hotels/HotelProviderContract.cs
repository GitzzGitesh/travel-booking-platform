using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Hotels.Mock;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.ProviderContracts.Hotels;

/// <summary>
/// The shared contract every <see cref="IHotelProvider"/> passes (ADR 0004, ADR 0030), mocks included: offers are the
/// provider's own, sellable as stated (a positive total, consistent cancellation terms, fees in the same currency, an
/// expiry in the future), and revalidating an offer returns it from the same provider with the same stay.
/// </summary>
public abstract class HotelProviderContract
{
    protected abstract IHotelProvider Provider { get; }

    protected abstract DateTimeOffset Now { get; }

    /// <summary>A destination the provider has availability for.</summary>
    protected abstract string Destination { get; }

    protected HotelSearchCriteria Criteria => new(Destination, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(40), DateOnly.FromDateTime(Now.UtcDateTime).AddDays(43), 2, [8]);

    [Fact]
    public async Task Offers_are_the_providers_own_and_sellable_as_stated()
    {
        var result = await Provider.SearchAsync(Criteria, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Offers.ShouldNotBeEmpty();
        foreach (var offer in result.Value.Offers)
        {
            offer.Reference.ProviderId.ShouldBe(Provider.Id);
            offer.Reference.Value.ShouldNotBeNullOrWhiteSpace();
            offer.TotalPrice.Amount.ShouldBeGreaterThan(0);
            offer.Cancellation.IsWellFormed(offer.TotalPrice).ShouldBeTrue();
            offer.IsSellable().ShouldBeTrue(); // the property (codes, star rating, zone, id length), board, room and fees
            offer.ExpiresAt.ShouldBeGreaterThan(Now);
            if (offer.FeesAtProperty is { } fees)
            {
                fees.Currency.ShouldBe(offer.TotalPrice.Currency);
            }

            offer.Property.Name.ShouldNotBeNullOrWhiteSpace();
            offer.Property.CountryCode.Length.ShouldBe(2);
            TimeZoneInfo.TryFindSystemTimeZoneById(offer.Property.TimeZone, out _).ShouldBeTrue();
            Enum.IsDefined(offer.Board).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Revalidating_returns_the_offer_from_the_same_provider_for_the_same_property()
    {
        var offer = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];

        var current = await Provider.RevalidateAsync(offer.Reference, Ct);

        current.IsSuccess.ShouldBeTrue();
        current.Value.Reference.ProviderId.ShouldBe(Provider.Id);
        current.Value.Property.Name.ShouldBe(offer.Property.Name);
        current.Value.TotalPrice.Currency.ShouldBe(offer.TotalPrice.Currency);
    }

    [Fact]
    public async Task A_reference_it_never_issued_is_an_invalid_request_never_an_exception()
    {
        var result = await Provider.RevalidateAsync(new HotelOfferRef(Provider.Id, "not-an-offer"), Ct);

        result.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task A_booking_is_made_once_per_client_reference_and_found_by_it()
    {
        var offer = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];
        var reference = Guid.NewGuid().ToString();
        var details = Details(offer, reference, "Lovelace");

        var first = await Provider.BookAsync(details, Ct);
        var again = await Provider.BookAsync(details, Ct);
        var found = await Provider.RetrieveBookingAsync(reference, Ct);

        first.IsSuccess.ShouldBeTrue();
        (first.Value.ClientReference, first.Value.ProviderId, first.Value.TotalPrice).ShouldBe((reference, Provider.Id, offer.TotalPrice));
        first.Value.ConfirmationNumber.ShouldNotBeNullOrWhiteSpace();
        again.Value.ShouldBe(first.Value); // never a second booking
        found.Value.Booking.ShouldBe(first.Value);
    }

    [Fact]
    public async Task Another_price_than_agreed_is_never_booked_and_an_unknown_reference_is_not_found()
    {
        var offer = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];
        var reference = Guid.NewGuid().ToString();

        var booked = await Provider.BookAsync(Details(offer, reference, "Lovelace") with { ExpectedTotalPrice = offer.TotalPrice with { Amount = offer.TotalPrice.Amount - 1m } }, Ct);

        booked.Error.Kind.ShouldBe(ProviderErrorKind.PriceChanged);
        (await Provider.RetrieveBookingAsync(reference, Ct)).Value.Booking.ShouldBeNull();
    }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The searched stay: two adults (the lead guest first) and one child.
    protected static HotelBookingDetails Details(HotelOffer offer, string reference, string leadSurname) => new(
        reference, offer.Reference, offer.TotalPrice,
        [new HotelGuest("Ada", leadSurname, null), new HotelGuest("George", "Byron", null), new HotelGuest("Allegra", "Byron", 8)],
        "guest@example.com", "+447700900123");
}

/// <summary>The mock passes the shared contract, and its scenarios behave as documented (MockHotelScenarios).</summary>
public sealed class MockHotelProviderTests : HotelProviderContract
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2027, 3, 1, 9, 0, 0, TimeSpan.Zero));

    private MockHotelProvider? _provider;

    // One instance per test: its bookings live in it, as in a host (a singleton).
    protected override IHotelProvider Provider => _provider ??= new MockHotelProvider(_clock);

    protected override DateTimeOffset Now => _clock.GetUtcNow();

    protected override string Destination => "PAR";

    [Theory]
    [InlineData(MockHotelScenarios.ExpiredDestination, ProviderErrorKind.OfferExpired)]
    [InlineData(MockHotelScenarios.SoldOutDestination, ProviderErrorKind.SoldOut)]
    public async Task Scenario_destinations_fail_revalidation_as_documented(string destination, ProviderErrorKind expected)
    {
        var offer = (await Provider.SearchAsync(Criteria with { Destination = destination }, Ct)).Value.Offers[0];

        (await Provider.RevalidateAsync(offer.Reference, Ct)).Error.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task The_price_changed_scenario_quotes_ten_percent_more_and_others_keep_their_price()
    {
        var changed = (await Provider.SearchAsync(Criteria with { Destination = MockHotelScenarios.PriceChangedDestination }, Ct)).Value.Offers[0];
        var same = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];

        (await Provider.RevalidateAsync(changed.Reference, Ct)).Value.TotalPrice.Amount.ShouldBe(decimal.Round(changed.TotalPrice.Amount * 1.1m, 2));
        var unchanged = (await Provider.RevalidateAsync(same.Reference, Ct)).Value;
        unchanged.TotalPrice.ShouldBe(same.TotalPrice);
        (unchanged.Board, unchanged.RoomDescription, unchanged.Cancellation).ShouldBe((same.Board, same.RoomDescription, same.Cancellation)); // unchanged terms: a confirmation, not a quote
    }

    [Fact]
    public async Task No_availability_and_an_unavailable_supplier_are_answers_not_exceptions()
    {
        (await Provider.SearchAsync(Criteria with { Destination = MockHotelScenarios.NoAvailabilityDestination }, Ct)).Value.Offers.ShouldBeEmpty();
        (await Provider.SearchAsync(Criteria with { Destination = MockHotelScenarios.UnavailableDestination }, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    [Fact]
    public async Task It_offers_a_refundable_a_non_refundable_and_a_rate_with_fees_at_the_property()
    {
        var offers = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers;

        offers.Count(o => o.Cancellation.Refundable).ShouldBe(2);
        offers.ShouldContain(o => !o.Cancellation.Refundable);
        offers.ShouldContain(o => o.FeesAtProperty != null);
        offers.ShouldAllBe(o => o.ExpiresAt == Now + MockHotelProvider.OfferLifetime);
    }

    [Theory]
    [InlineData(MockHotelBookingScenarios.RejectedSurname, ProviderErrorKind.Rejected, false)]
    [InlineData(MockHotelBookingScenarios.TimeoutNotBookedSurname, ProviderErrorKind.Unknown, false)]
    [InlineData(MockHotelBookingScenarios.TimeoutBookedSurname, ProviderErrorKind.Unknown, true)]
    public async Task Booking_scenarios_refuse_or_time_out_and_a_lookup_tells_what_happened(string surname, ProviderErrorKind kind, bool booked)
    {
        var offer = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];
        var reference = Guid.NewGuid().ToString();

        (await Provider.BookAsync(Details(offer, reference, surname), Ct)).Error.Kind.ShouldBe(kind);

        ((await Provider.RetrieveBookingAsync(reference, Ct)).Value.Booking is not null).ShouldBe(booked);
    }

    [Fact]
    public async Task The_price_mismatch_scenario_books_at_another_price_and_guests_must_match_the_stay()
    {
        var offer = (await Provider.SearchAsync(Criteria, Ct)).Value.Offers[0];

        (await Provider.BookAsync(Details(offer, Guid.NewGuid().ToString(), MockHotelBookingScenarios.PriceMismatchSurname), Ct)).Value.TotalPrice.ShouldNotBe(offer.TotalPrice);
        (await Provider.BookAsync(Details(offer, Guid.NewGuid().ToString(), "Lovelace") with { Guests = [new HotelGuest("Ada", "Lovelace", null)] }, Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        var teenAsAdult = Details(offer, Guid.NewGuid().ToString(), "Lovelace");
        (await Provider.BookAsync(teenAsAdult with { Guests = [.. teenAsAdult.Guests.Select(g => g with { ChildAge = null })] }, Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest); // the child priced at 8 booked as a third adult
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Test")]
    public void It_never_runs_outside_development_or_staging(string environment)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FixedEnvironment(environment))
            .AddMockHotelProvider(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<MockHotelProviderOptions>>().Value);
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
