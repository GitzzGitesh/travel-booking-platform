using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>
/// Supplier booking outcomes (ADR 0005, booking-lifecycle.md): definitive failures vs an explicit Unknown, one supplier
/// write per call, reconciliation by our reference, and bookings checked against what was agreed.
/// </summary>
public sealed class FlightSupplierBookingTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly ClientReference _reference = new("order-item-42");
    private static readonly Money _agreed = new(270m, new CurrencyCode("XTS"));

    private static readonly FlightBookingDetails _details = new(
        _reference,
        new ProviderOfferRef("stub", "token"),
        _agreed,
        [new FlightPassenger(PassengerType.Adult, "Test", "Private-Name")]);

    private static readonly FlightBookingConfirmation _confirmation = new(_reference, new ProviderBookingRef("stub", "ABC234"), _agreed);

    [Fact]
    public async Task A_confirmed_booking_at_the_agreed_price_is_Booked()
    {
        var provider = new StubProvider { Book = Success(_confirmation) };

        var outcome = await Booking(provider).BookAsync(_details, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.ShouldBe(_confirmation);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Rejected, SupplierBookingFailureReason.Rejected)]
    [InlineData(ProviderErrorKind.PriceChanged, SupplierBookingFailureReason.PriceChanged)]
    [InlineData(ProviderErrorKind.SoldOut, SupplierBookingFailureReason.SoldOut)]
    [InlineData(ProviderErrorKind.OfferExpired, SupplierBookingFailureReason.OfferExpired)]
    [InlineData(ProviderErrorKind.InvalidRequest, SupplierBookingFailureReason.InvalidRequest)]
    internal async Task Definitive_supplier_refusals_are_NotBooked(ProviderErrorKind kind, SupplierBookingFailureReason reason)
    {
        var provider = new StubProvider { Book = Failure(kind) };

        var outcome = await Booking(provider).BookAsync(_details, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.NotBooked>().Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unknown)]
    [InlineData(ProviderErrorKind.Unavailable)] // a gateway 5xx may follow a processed request: not proof of no booking
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.AuthFailure)]
    public async Task Ambiguous_write_failures_are_Unknown_logged_without_PII_and_never_resubmitted(ProviderErrorKind kind)
    {
        var provider = new StubProvider { Book = Failure(kind) };
        var logger = new RecordingLogger();

        var outcome = await new FlightSupplierBooking(new FlightProviders([provider]), new FakeTimeProvider(_now), logger).BookAsync(_details, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Unknown>().Cause.ShouldBe(kind);
        provider.BookCalls.ShouldBe(1);
        var warning = logger.Messages.ShouldHaveSingleItem();
        warning.ShouldContain("order-item-42");
        warning.ShouldNotContain("Private-Name");
    }

    [Fact]
    public void Only_the_five_definitive_refusals_are_not_unknown()
    {
        ProviderErrorKind[] definitive = [ProviderErrorKind.Rejected, ProviderErrorKind.PriceChanged, ProviderErrorKind.SoldOut, ProviderErrorKind.OfferExpired, ProviderErrorKind.InvalidRequest];

        foreach (var kind in Enum.GetValues<ProviderErrorKind>())
        {
            (FlightSupplierBooking.Classify(kind) is SupplierBookingOutcome.Unknown).ShouldBe(!definitive.Contains(kind), kind.ToString());
        }
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException))] // an HttpClient timeout
    [InlineData(typeof(HttpRequestException))] // a reset connection
    [InlineData(typeof(OperationCanceledException))] // the caller cancelled after the request was sent
    public async Task An_exception_from_a_started_write_is_Unknown_never_a_failure(Type exception)
    {
        var provider = new StubProvider { Throw = (Exception)Activator.CreateInstance(exception)! };

        var outcome = await Booking(provider).BookAsync(_details, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Unknown>();
        provider.BookCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelled_before_sending_attempts_nothing()
    {
        var provider = new StubProvider { Book = Success(_confirmation) };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Booking(provider).BookAsync(_details, cancelled.Token));
        provider.BookCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_offer_from_another_provider_is_never_sent()
    {
        var provider = new StubProvider { Book = Success(_confirmation) };

        var outcome = await Booking(provider).BookAsync(_details with { Offer = new ProviderOfferRef("other", "token") }, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.NotBooked>().Reason.ShouldBe(SupplierBookingFailureReason.InvalidRequest);
        provider.BookCalls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(NotAsAgreed))]
    public async Task A_booking_not_as_agreed_is_a_Mismatch_never_Booked(string _, FlightBookingConfirmation confirmation)
    {
        var provider = new StubProvider { Book = Success(confirmation) };

        var outcome = await Booking(provider).BookAsync(_details, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Mismatch>();
    }

    public static TheoryData<string, FlightBookingConfirmation> NotAsAgreed() => new()
    {
        { "another price", _confirmation with { TotalPrice = new Money(299m, new CurrencyCode("XTS")) } },
        { "another reference", _confirmation with { ClientReference = new ClientReference("someone-else") } },
        { "another provider", _confirmation with { Booking = new ProviderBookingRef("other", "ABC234") } },
    };

    [Fact]
    public async Task Reconciliation_finds_a_booking_made_before_the_timeout()
    {
        var provider = new StubProvider { Lookup = Result<FlightBookingLookup, ProviderError>.Success(new FlightBookingLookup(_confirmation)) };

        var outcome = await Booking(provider).ReconcileAsync("stub", _reference, _agreed, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Booked>().Confirmation.Booking.Value.ShouldBe("ABC234");
        provider.LookedUp.ShouldBe(_reference);
        provider.BookCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_checks_a_found_booking_against_the_agreed_price()
    {
        var provider = new StubProvider { Lookup = Result<FlightBookingLookup, ProviderError>.Success(new FlightBookingLookup(_confirmation)) };

        var outcome = await Booking(provider).ReconcileAsync("stub", _reference, new Money(1m, new CurrencyCode("XTS")), TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Mismatch>();
    }

    [Fact]
    public async Task Reconciliation_that_finds_nothing_is_NotFound_as_of_now_not_a_definitive_failure()
    {
        var provider = new StubProvider { Lookup = Result<FlightBookingLookup, ProviderError>.Success(new FlightBookingLookup(null)) };

        var outcome = await Booking(provider).ReconcileAsync("stub", _reference, _agreed, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.NotFound>().At.ShouldBe(_now);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unavailable)]
    [InlineData(ProviderErrorKind.Unknown)]
    [InlineData(ProviderErrorKind.RateLimited)]
    public async Task A_failed_lookup_leaves_the_outcome_Unknown(ProviderErrorKind kind)
    {
        var provider = new StubProvider { Lookup = Result<FlightBookingLookup, ProviderError>.Failure(new ProviderError(kind, "stub")) };

        var outcome = await Booking(provider).ReconcileAsync("stub", _reference, _agreed, TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<SupplierBookingOutcome.Unknown>().Cause.ShouldBe(kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ümlaut")]
    public void Client_references_are_short_ascii_identifiers(string value) =>
        Should.Throw<ArgumentException>(() => new ClientReference(value));

    [Fact]
    public void A_client_reference_is_at_most_64_characters()
    {
        Should.NotThrow(() => new ClientReference(new string('a', 64)));
        Should.Throw<ArgumentException>(() => new ClientReference(new string('a', 65)));
    }

    private static FlightSupplierBooking Booking(IFlightProvider provider) => new(new FlightProviders([provider]), new FakeTimeProvider(_now), new RecordingLogger());

    private static Result<FlightBookingConfirmation, ProviderError> Success(FlightBookingConfirmation confirmation) =>
        Result<FlightBookingConfirmation, ProviderError>.Success(confirmation);

    private static Result<FlightBookingConfirmation, ProviderError> Failure(ProviderErrorKind kind) =>
        Result<FlightBookingConfirmation, ProviderError>.Failure(new ProviderError(kind, "stub"));

    private sealed class StubProvider : IFlightProvider
    {
        public Result<FlightBookingConfirmation, ProviderError>? Book { get; init; }

        public Exception? Throw { get; init; }

        public Result<FlightBookingLookup, ProviderError>? Lookup { get; init; }

        public int BookCalls { get; private set; }

        public ClientReference? LookedUp { get; private set; }

        public string Id => "stub";

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken)
        {
            BookCalls++;
            return Throw is null ? Task.FromResult(Book!) : Task.FromException<Result<FlightBookingConfirmation, ProviderError>>(Throw);
        }

        public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken)
        {
            LookedUp = clientReference;
            return Task.FromResult(Lookup!);
        }
    }

    private sealed class RecordingLogger : ILogger<FlightSupplierBooking>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
