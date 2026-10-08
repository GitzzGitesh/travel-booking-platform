using Microsoft.Extensions.Logging.Abstractions;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Application;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Hotels.Domain;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.UnitTests;

/// <summary>
/// Booking a hotel selection for Orders (ADR 0030 §6, F-10..F-12): nothing is sent for a request that does not match the
/// customer's confirmed stay; a definitive refusal is NotBooked; anything ambiguous is Unknown (looked up, never resent);
/// a booking not as agreed is a Mismatch, never Booked.
/// </summary>
public sealed class HotelBookingsTests
{
    private static readonly DateTimeOffset _now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyCode _xts = new("XTS");
    private static readonly HotelSearchCriteria _stay = new("PAR", new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 13), 2, [1, 8, 15]);

    private readonly ScriptedProvider _provider = new();
    private readonly HotelSelection _selection;
    private readonly HotelBookings _bookings;

    public HotelBookingsTests()
    {
        _selection = HotelSelection.Select(Guid.NewGuid(), Guid.NewGuid(), _stay, Offer(300m), _now, "cust-1");
        _selection.Revalidate(Offer(300m), _now.AddMinutes(1));
        _bookings = new HotelBookings(new Store(_selection), new HotelProviders([_provider]), NullLogger<HotelBookings>.Instance);
    }

    [Fact]
    public void Guests_are_counted_by_the_travellers_age_rule()
    {
        // Two adults, and children aged 1 (an infant), 8 (a child) and 15 (counted as an adult, as airlines and Customers do).
        HotelSelections.Guests(_selection).ShouldBe((3, 1, 1));
    }

    [Fact]
    public async Task A_matching_request_books_once_under_our_reference_at_the_agreed_price()
    {
        var result = await _bookings.BookAsync(Request(), Ct);

        result.ShouldBe(new HotelBookingResult(HotelBookingStatus.Booked, "stub", "CONF-1"));
        var sent = _provider.Sent.ShouldHaveSingleItem();
        sent.Guests.Select(g => g.ChildAge).ShouldBe([null, null, 15, 8, 1]);
        (sent.ClientReference, sent.ExpectedTotalPrice, sent.Offer.Value).ShouldBe(("item-1", Money(300m), "token-300"));
    }

    [Fact]
    public async Task A_booked_stay_freezes_its_selection_and_its_facts_are_readable()
    {
        await _bookings.BookAsync(Request(), Ct);

        _selection.Status.ShouldBe(HotelSelectionStatus.Booked);
        var stay = (await new HotelStays(new Store(_selection)).GetStayAsync(_selection.Id, Ct)).ShouldNotBeNull();
        (stay.PropertyName, stay.Nights, stay.Room, stay.Board, stay.Booked).ShouldBe(("Hotel A", 3, "Double room", "Breakfast", true));
        stay.Cancellation.PenaltyAfterDeadline.ShouldBe(Money(100m));
    }

    [Fact]
    public async Task Nothing_is_sent_for_another_customer_an_unconfirmed_selection_or_guests_that_do_not_match_the_stay()
    {
        (await _bookings.BookAsync(Request() with { CustomerId = "cust-2" }, Ct)).Status.ShouldBe(HotelBookingStatus.NotBooked);
        (await _bookings.BookAsync(Request() with { Guests = [.. Request().Guests.Skip(1)] }, Ct)).Status.ShouldBe(HotelBookingStatus.NotBooked);
        (await _bookings.BookAsync(Request() with { Guests = [.. Request().Guests.Reverse()] }, Ct)).Status.ShouldBe(HotelBookingStatus.NotBooked); // the lead guest is an adult
        (await _bookings.BookAsync(Request() with { Guests = [.. Request().Guests.Select(g => g.ChildAge == 15 ? g with { ChildAge = null } : g)] }, Ct))
            .Status.ShouldBe(HotelBookingStatus.NotBooked); // the teenager as a third adult: not the priced occupancy
        (await _bookings.BookAsync(Request() with { Guests = [.. Request().Guests.Select(g => g.ChildAge == 8 ? g with { ChildAge = 9 } : g)] }, Ct))
            .Status.ShouldBe(HotelBookingStatus.NotBooked); // another age than searched
        (await _bookings.BookAsync(Request() with { ClientReference = "not a reference" }, Ct)).Status.ShouldBe(HotelBookingStatus.NotBooked);
        _provider.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ProviderErrorKind.Rejected, HotelBookingStatus.NotBooked)]
    [InlineData(ProviderErrorKind.PriceChanged, HotelBookingStatus.NotBooked)]
    [InlineData(ProviderErrorKind.SoldOut, HotelBookingStatus.NotBooked)]
    [InlineData(ProviderErrorKind.Unknown, HotelBookingStatus.Unknown)]
    [InlineData(ProviderErrorKind.Unavailable, HotelBookingStatus.Unknown)]
    [InlineData(ProviderErrorKind.RateLimited, HotelBookingStatus.Unknown)]
    public async Task Only_a_definitive_refusal_is_not_booked_anything_else_is_unknown(ProviderErrorKind kind, HotelBookingStatus expected)
    {
        _provider.Failure = new ProviderError(kind, "supplier said no");

        var result = await _bookings.BookAsync(Request(), Ct);

        result.Status.ShouldBe(expected);
        result.Detail?.ShouldNotContain("supplier said no"); // our wording, never the supplier's
    }

    [Fact]
    public async Task A_throwing_write_is_unknown_and_the_selection_is_never_sent_twice()
    {
        _provider.Throws = true;
        (await _bookings.BookAsync(Request(), Ct)).Status.ShouldBe(HotelBookingStatus.Unknown);
        _selection.Status.ShouldBe(HotelSelectionStatus.Booking); // frozen: a price check now gets 409

        _provider.Throws = false;
        (await _bookings.BookAsync(Request(), Ct)).Status.ShouldBe(HotelBookingStatus.NotBooked); // one write per selection, ever
        _provider.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_booking_not_as_agreed_is_a_mismatch_and_never_marked_booked()
    {
        _provider.BookedPrice = Money(301m);

        (await _bookings.BookAsync(Request(), Ct)).ShouldBe(new HotelBookingResult(HotelBookingStatus.Mismatch, "stub", "CONF-1"));
        (await _bookings.ReconcileAsync(_selection.Id, "cust-1", "item-1", Money(300m), Ct)).Status.ShouldBe(HotelBookingStatus.Mismatch);
        _selection.Status.ShouldBe(HotelSelectionStatus.Booking); // still frozen; a person decides
    }

    [Fact]
    public async Task A_booking_without_a_reference_is_unknown_not_a_mismatch()
    {
        _provider.Reference = " ";

        (await _bookings.BookAsync(Request(), Ct)).ShouldBe(new HotelBookingResult(HotelBookingStatus.Unknown, "stub"));
    }

    [Fact]
    public async Task A_lookup_finds_the_booking_or_nothing_and_a_failed_lookup_concludes_nothing()
    {
        (await _bookings.ReconcileAsync(_selection.Id, "cust-1", "item-1", Money(300m), Ct)).Status.ShouldBe(HotelBookingStatus.NotFound);

        await _bookings.BookAsync(Request(), Ct);
        (await _bookings.ReconcileAsync(_selection.Id, "cust-1", "item-1", Money(300m), Ct)).ShouldBe(new HotelBookingResult(HotelBookingStatus.Booked, "stub", "CONF-1"));

        _provider.LookupFails = true;
        (await _bookings.ReconcileAsync(_selection.Id, "cust-1", "item-1", Money(300m), Ct)).Status.ShouldBe(HotelBookingStatus.Unknown);
        (await _bookings.ReconcileAsync(_selection.Id, "cust-2", "item-1", Money(300m), Ct)).Status.ShouldBe(HotelBookingStatus.Unknown);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Money Money(decimal amount) => new(amount, _xts);

    private HotelBookingRequest Request() => new(
        _selection.Id,
        "cust-1",
        "item-1",
        Money(300m),
        [
            new HotelBookingGuest("Ada", "Lovelace", null),
            new HotelBookingGuest("George", "Byron", null),
            new HotelBookingGuest("Alex", "Byron", 15), // a teenager is a child of 15 to the property, never an adult
            new HotelBookingGuest("Allegra", "Byron", 8),
            new HotelBookingGuest("Ida", "Byron", 1),
        ],
        new HotelBookingContact("guest@example.com", "+447700900123"));

    private static HotelOffer Offer(decimal total) => new(
        new HotelOfferRef("stub", $"token-{total}"),
        new HotelProperty("p-1", "Hotel A", "1 Street", "PAR", "FR", 4m, "Europe/Paris"),
        "Double room",
        BoardBasis.Breakfast,
        Money(total),
        null,
        CancellationPolicy.FreeUntil(_now.AddDays(30), Money(100m)),
        _now.AddMinutes(30));

    private sealed class ScriptedProvider : IHotelProvider
    {
        private readonly Dictionary<string, HotelBookingConfirmation> _booked = [];

        public List<HotelBookingDetails> Sent { get; } = [];

        public ProviderError? Failure { get; set; }

        public bool Throws { get; set; }

        public bool LookupFails { get; set; }

        public Money? BookedPrice { get; set; }

        public string Reference { get; set; } = "CONF-1";

        public string Id => "stub";

        public Task<Result<HotelSearchResult, ProviderError>> SearchAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<HotelOffer, ProviderError>> RevalidateAsync(HotelOfferRef offer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<HotelBookingConfirmation, ProviderError>> BookAsync(HotelBookingDetails details, CancellationToken cancellationToken)
        {
            Sent.Add(details);
            if (Throws)
            {
                throw new HttpRequestException("connection reset");
            }

            if (Failure is { } failure)
            {
                return Task.FromResult(Result<HotelBookingConfirmation, ProviderError>.Failure(failure));
            }

            var confirmation = new HotelBookingConfirmation(details.ClientReference, Id, Reference, BookedPrice ?? details.ExpectedTotalPrice);
            _booked[details.ClientReference] = confirmation;
            return Task.FromResult(Result<HotelBookingConfirmation, ProviderError>.Success(confirmation));
        }

        public Task<Result<HotelBookingLookup, ProviderError>> RetrieveBookingAsync(string clientReference, CancellationToken cancellationToken) =>
            Task.FromResult(LookupFails
                ? Result<HotelBookingLookup, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "down"))
                : Result<HotelBookingLookup, ProviderError>.Success(new HotelBookingLookup(_booked.GetValueOrDefault(clientReference))));
    }

    private sealed class Store(HotelSelection selection) : IHotelSelectionStore
    {
        public Task<HotelSelection?> FindAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAddAsync(HotelSelection added, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HotelSelection?> FindForUpdateAsync(Guid selectionId, CancellationToken cancellationToken) =>
            Task.FromResult<HotelSelection?>(selectionId == selection.Id ? selection : null);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<HotelSelection?> FindByIdAsync(Guid selectionId, CancellationToken cancellationToken) => FindForUpdateAsync(selectionId, cancellationToken);
    }
}
