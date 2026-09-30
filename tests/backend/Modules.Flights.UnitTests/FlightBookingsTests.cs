using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>
/// The booking contract Orders calls (ADR 0021): only the customer's own confirmed selection is sent, to the supplier that
/// offered it, under our reference at the agreed price; supplier outcomes arrive in the core's terms only.
/// </summary>
public sealed class FlightBookingsTests
{
    private static readonly Money _agreed = new(270m, new CurrencyCode("XTS"));
    private readonly FakeTimeProvider _clock = new(SelectedOfferStateTests.Now);
    private readonly RecordingProvider _provider = new();

    [Fact]
    public async Task The_customers_confirmed_selection_is_booked_under_our_reference_with_its_travellers_and_contact()
    {
        var selection = Confirmed();
        var itemId = Guid.NewGuid().ToString();

        var result = await Bookings(selection).BookAsync(Request(selection, itemId), TestContext.Current.CancellationToken);

        result.ShouldBe(new FlightBookingResult(FlightBookingStatus.Booked, "stub", "PNR123", FlightTicketingStatus.Issued));
        var sent = _provider.Sent.ShouldHaveSingleItem();
        (sent.ClientReference.Value, sent.Offer, sent.ExpectedTotalPrice).ShouldBe((itemId, new ProviderOfferRef("stub", "stub-token-1"), _agreed));
        var passenger = sent.Passengers.ShouldHaveSingleItem();
        (passenger.FamilyName, passenger.Gender, passenger.Document!.Number).ShouldBe(("Lovelace", PassengerGender.Female, "P1234567"));
        sent.Contact!.Email.ShouldBe("ada@example.com");
        passenger.ToString().ShouldNotContain("Lovelace");
        sent.Contact.ToString().ShouldNotContain("ada@");
    }

    [Fact]
    public async Task Another_customers_or_an_unconfirmed_selection_is_never_sent()
    {
        var selection = Confirmed();
        var unconfirmed = SelectedOfferStateTests.NewSelection();

        var other = await Bookings(selection).BookAsync(Request(selection, "item-1") with { CustomerId = "someone-else" }, TestContext.Current.CancellationToken);
        var notConfirmed = await Bookings(unconfirmed).BookAsync(Request(unconfirmed, "item-2"), TestContext.Current.CancellationToken);

        (other.Status, notConfirmed.Status).ShouldBe((FlightBookingStatus.NotBooked, FlightBookingStatus.NotBooked));
        _provider.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_supplier_outcome_stays_unknown_and_a_lookup_goes_to_the_selections_supplier_by_our_reference()
    {
        var selection = Confirmed();
        _provider.Book = Result<FlightBookingConfirmation, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "timeout"));

        var booked = await Bookings(selection).BookAsync(Request(selection, "item-1"), TestContext.Current.CancellationToken);
        var found = await Bookings(selection).ReconcileAsync(selection.Id, SelectedOfferStateTests.Customer, "item-1", _agreed, TestContext.Current.CancellationToken);

        booked.Status.ShouldBe(FlightBookingStatus.Unknown);
        found.Status.ShouldBe(FlightBookingStatus.NotFound);
        _provider.LookedUp.ShouldBe("item-1");
        _provider.Sent.Count.ShouldBe(1); // a lookup never books
    }

    private static SelectedOffer Confirmed()
    {
        var offer = SelectedOfferStateTests.NewSelection();
        offer.Revalidate(SelectedOfferStateTests.Current(270m), SelectedOfferStateTests.Now);
        return offer;
    }

    private static FlightBookingRequest Request(SelectedOffer selection, string itemId) => new(
        selection.Id,
        SelectedOfferStateTests.Customer,
        itemId,
        _agreed,
        [new FlightBookingPassenger(FlightPassengerKind.Adult, "Ada", "Lovelace", new DateOnly(1990, 12, 10), FlightPassengerGender.Female,
            new FlightBookingDocument(FlightTravelDocumentKind.Passport, "P1234567", "GB", "GB", new DateOnly(2035, 1, 1)))],
        new Contracts.FlightBookingContact("ada@example.com", "+447700900123"));

    private FlightBookings Bookings(SelectedOffer selection) =>
        new(new OneOfferStore(selection), new FlightSupplierBooking(new FlightProviders([_provider]), _clock, NullLogger<FlightSupplierBooking>.Instance));

    private sealed class RecordingProvider : IFlightProvider
    {
        public Result<FlightBookingConfirmation, ProviderError>? Book { get; set; }

        public List<FlightBookingDetails> Sent { get; } = [];

        public string? LookedUp { get; private set; }

        public string Id => "stub";

        public FlightProviderCapabilities Capabilities => TestCapabilities.All;

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken)
        {
            Sent.Add(details);
            return Task.FromResult(Book ?? Result<FlightBookingConfirmation, ProviderError>.Success(
                new FlightBookingConfirmation(details.ClientReference, new ProviderBookingRef(Id, "PNR123"), details.ExpectedTotalPrice, TicketingStatus.Issued)));
        }

        public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken)
        {
            LookedUp = clientReference.Value;
            return Task.FromResult(Result<FlightBookingLookup, ProviderError>.Success(new FlightBookingLookup(null)));
        }
    }

    private sealed class OneOfferStore(SelectedOffer offer) : ISelectedOfferStore
    {
        public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SelectedOffer?> FindByIdAsync(Guid selectedOfferId, CancellationToken cancellationToken) => Task.FromResult(selectedOfferId == offer.Id ? offer : null);

        public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
