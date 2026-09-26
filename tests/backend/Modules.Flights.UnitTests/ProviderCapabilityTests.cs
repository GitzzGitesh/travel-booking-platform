using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>
/// Provider capabilities and composition (Q6): the core never calls an operation an adapter does not implement, never
/// routes to another provider, searches exactly one configured provider, and runs only ProductionReady adapters outside
/// Development and Staging.
/// </summary>
public sealed class ProviderCapabilityTests
{
    private static readonly DateTimeOffset _now = SelectedOfferStateTests.Now;

    private static readonly FlightProviderCapabilities _searchOnly = new(
        AdapterStage.MappedFromDocumentation, [ProviderOperation.Search], new Dictionary<FlightCapability, CapabilityDeclaration>());

    private static readonly FlightProviderCapabilities _scaffolded = new(AdapterStage.Scaffolded, [], new Dictionary<FlightCapability, CapabilityDeclaration>());

    [Fact]
    public async Task An_offer_is_not_revalidated_by_its_provider_when_the_adapter_cannot_revalidate_and_never_by_another()
    {
        var offer = SelectedOfferStateTests.NewSelection(); // offered by "stub"
        var owner = new StubProvider("stub", _searchOnly);
        var other = new StubProvider("other", FlightProviderCapabilities.NotDeclared);
        var handler = new RevalidateSelectedOfferHandler(new OneOfferStore(offer), new FlightProviders([owner, other], "stub"), new FakeTimeProvider(_now));

        var result = await handler.HandleAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SelectedOfferFailure.ProviderFailed>().Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
        (owner.Calls, other.Calls).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_booking_or_lookup_the_adapter_does_not_implement_is_refused_unsent()
    {
        var owner = new StubProvider("stub", _searchOnly);
        var booking = new FlightSupplierBooking(new FlightProviders([owner]), new FakeTimeProvider(_now), NullLogger<FlightSupplierBooking>.Instance);
        var details = new FlightBookingDetails(new ClientReference("item-1"), new ProviderOfferRef("stub", "token"), new Money(270m, new CurrencyCode("XTS")), [new FlightPassenger(PassengerType.Adult, "Test", "Traveller")]);

        (await booking.BookAsync(details, TestContext.Current.CancellationToken)).ShouldBe(new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.InvalidRequest));
        (await booking.ReconcileAsync("stub", details.ClientReference, details.ExpectedTotalPrice, TestContext.Current.CancellationToken))
            .ShouldBe(new SupplierBookingOutcome.Unknown(ProviderErrorKind.Unavailable));
        owner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Checkout_revalidation_refuses_an_offer_its_supplier_cannot_book_before_any_payment()
    {
        var offer = SelectedOfferStateTests.NewSelection(); // offered by "stub"
        var searchOnly = new StubProvider("stub", _searchOnly);
        var providers = new FlightProviders([searchOnly], "stub");
        var store = new OneOfferStore(offer);
        var selections = new FlightSelections(store, new RevalidateSelectedOfferHandler(store, providers, new FakeTimeProvider(_now)), providers, new FakeTimeProvider(_now));

        var result = await selections.RevalidateAsync(offer.Id, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(TravelBooking.Modules.Flights.Contracts.FlightSelectionUnavailable.SupplierCannotBook);
        searchOnly.Calls.ShouldBe(0); // not even revalidated: nothing proceeds towards payment
    }

    [Fact]
    public void A_search_provider_must_implement_search()
    {
        new FlightProviders([new StubProvider("duffel", _searchOnly), new StubProvider("sabre", _scaffolded)], "duffel").SearchProviderProblem().ShouldBeNull();
        new FlightProviders([new StubProvider("duffel", _searchOnly), new StubProvider("sabre", _scaffolded)], "sabre").SearchProviderProblem().ShouldNotBeNull().ShouldContain("does not implement search");
        new FlightProviders([new StubProvider("sabre", _scaffolded)]).SearchProviderProblem().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    public void Only_production_ready_adapters_run_outside_development_and_staging(string environment, bool valid)
    {
        var validator = new FlightProviderCompositionValidator([new StubProvider("duffel", _searchOnly)], new HostingEnvironment { EnvironmentName = environment });

        validator.Validate(null, new FlightProviderComposition()).Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void A_production_ready_adapter_runs_in_production_and_no_provider_at_all_is_allowed()
    {
        var ready = new FlightProviderCapabilities(AdapterStage.ProductionReady, [ProviderOperation.Search], new Dictionary<FlightCapability, CapabilityDeclaration>());
        var production = new HostingEnvironment { EnvironmentName = Environments.Production };

        new FlightProviderCompositionValidator([new StubProvider("real", ready)], production).Validate(null, new()).Succeeded.ShouldBeTrue();
        new FlightProviderCompositionValidator([], production).Validate(null, new()).Succeeded.ShouldBeTrue(); // no flight search here
    }

    [Fact]
    public void A_bad_composition_fails_at_startup_with_the_reason()
    {
        var development = new HostingEnvironment { EnvironmentName = Environments.Development };

        var duplicate = new FlightProviderCompositionValidator([new StubProvider("a", _searchOnly), new StubProvider("a", _searchOnly)], development).Validate(null, new());
        var unnamed = new FlightProviderCompositionValidator([new StubProvider("a", _searchOnly), new StubProvider("b", _searchOnly)], development).Validate(null, new());
        var unknown = new FlightProviderCompositionValidator([new StubProvider("a", _searchOnly)], development).Validate(null, new() { SearchProviderId = "b" });

        duplicate.FailureMessage.ShouldNotBeNull().ShouldContain("share the id");
        unnamed.FailureMessage.ShouldNotBeNull().ShouldContain("SearchProviderId");
        unknown.FailureMessage.ShouldNotBeNull().ShouldContain("not composed");
    }

    [Fact]
    public void Declarations_start_unconfirmed_and_are_revised_by_configuration_with_bad_names_reported()
    {
        var declared = new FlightProviderCapabilities(AdapterStage.Scaffolded, [], new Dictionary<FlightCapability, CapabilityDeclaration>
        {
            [FlightCapability.Search] = new(CapabilitySupport.Supported),
        });

        declared.Get(FlightCapability.Ticketing).Support.ShouldBe(CapabilitySupport.RequiresConfirmation); // never assumed

        var revised = declared.WithOverrides([new("Ticketing", "Unsupported"), new("Search", null)]);
        (revised.Get(FlightCapability.Ticketing).Support, revised.Get(FlightCapability.Search).Support).ShouldBe((CapabilitySupport.Unsupported, CapabilitySupport.Supported));
        Should.Throw<ArgumentException>(() => declared.WithOverrides([new("Teleport", "Supported")]));
        Should.Throw<ArgumentException>(() => declared.WithOverrides([new("Ticketing", "Maybe")]));
        FlightProviderCapabilities.NotDeclared.Implemented.ShouldBeEmpty(); // declares nothing: implements nothing (fail closed)
    }

    private sealed class StubProvider(string id, FlightProviderCapabilities capabilities) : IFlightProvider
    {
        public int Calls { get; private set; }

        public string Id => id;

        public FlightProviderCapabilities Capabilities => capabilities;

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) => Called<FlightSearchResult>();

        public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken) => Called<FlightOffer>();

        public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken) => Called<FlightBookingConfirmation>();

        public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken) => Called<FlightBookingLookup>();

        private Task<Result<T, ProviderError>> Called<T>()
            where T : notnull
        {
            Calls++;
            throw new InvalidOperationException("The core must not call this operation.");
        }
    }

    private sealed class OneOfferStore(SelectedOffer offer) : ISelectedOfferStore
    {
        public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindByIdAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult<SelectedOffer?>(selectedOfferId == offer.Id ? offer : null);

        public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult<SelectedOffer?>(selectedOfferId == offer.Id ? offer : null);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
