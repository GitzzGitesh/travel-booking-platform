using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Mock;

/// <summary>
/// Deterministic flight offers: the same criteria and clock always give the same offers, with no randomness.
/// Prices use XTS, the ISO 4217 code reserved for testing, so no charge currency is implied (Q5 is open).
/// The fictional carrier ZZ and fixed timetable are not real schedules; arrival times ignore time-zone differences.
/// </summary>
internal sealed class MockFlightProvider(IOptions<MockFlightProviderOptions> options, TimeProvider timeProvider) : IFlightProvider
{
    public const string ProviderId = "mock";
    private const string _carrier = "ZZ";
    private static readonly CurrencyCode _testCurrency = new("XTS");
    private static readonly TimeSpan _offerLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _flightDuration = new(2, 15, 0);
    private static readonly TimeOnly[] _departureTimes = [new(7, 5), new(12, 40), new(18, 25)];

    public string Id => ProviderId;

    public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = options.Value.Scenario switch
        {
            MockFlightScenario.Success => Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult(CreateOffers(criteria))),
            MockFlightScenario.NoResults => Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])),
            MockFlightScenario.Unavailable => Failure(ProviderErrorKind.Unavailable, "Mock provider unavailable (scenario)."),
            MockFlightScenario.RateLimited => Failure(ProviderErrorKind.RateLimited, "Mock provider rate limited (scenario)."),
            _ => throw new InvalidOperationException($"Unknown mock scenario {options.Value.Scenario}."),
        };

        return Task.FromResult(result);
    }

    private List<FlightOffer> CreateOffers(FlightSearchCriteria criteria)
    {
        var expiresAt = timeProvider.GetUtcNow() + _offerLifetime;
        var routeSeed = StableHash($"{criteria.Origin}{criteria.Destination}");
        var passengers = criteria.Passengers;

        return _departureTimes.Select((time, index) =>
        {
            var outbound = Slice(criteria.Origin, criteria.Destination, criteria.DepartureDate, time, routeSeed, index);
            IReadOnlyList<FlightSlice> slices = criteria.ReturnDate is { } returnDate
                ? [outbound, Slice(criteria.Destination, criteria.Origin, returnDate, time, routeSeed + 500, index)]
                : [outbound];

            var fare = FarePerSeat(criteria.Cabin, routeSeed, index) * slices.Count;
            var total = (fare * passengers.SeatedPassengers) + (fare * 0.1m * passengers.Infants);

            // Everything that determines the price is in the reference, so a later revalidate can reprice it statelessly.
            var reference = new ProviderOfferRef(
                ProviderId,
                $"mock-{index}-{criteria.Origin}{criteria.Destination}-{criteria.DepartureDate:yyyyMMdd}-{criteria.ReturnDate:yyyyMMdd}-{criteria.Cabin}-{passengers.Adults}A{passengers.Children}C{passengers.Infants}I");

            return new FlightOffer(reference, new Money(total, _testCurrency), expiresAt, slices);
        }).ToList();
    }

    private static FlightSlice Slice(AirportCode from, AirportCode to, DateOnly date, TimeOnly time, int routeSeed, int index)
    {
        var departure = date.ToDateTime(time, DateTimeKind.Unspecified);
        var flightNumber = $"{_carrier}{100 + ((routeSeed + (index * 7)) % 900)}";
        return new FlightSlice([new FlightSegment(_carrier, flightNumber, from, to, departure, departure + _flightDuration)]);
    }

    private static decimal FarePerSeat(CabinClass cabin, int routeSeed, int index)
    {
        var baseFare = 80m + (routeSeed % 60) + (index * 25m);
        return baseFare * cabin switch
        {
            CabinClass.PremiumEconomy => 1.6m,
            CabinClass.Business => 3m,
            CabinClass.First => 5m,
            _ => 1m,
        };
    }

    // string.GetHashCode is randomised per process; this is stable across runs and machines.
    private static int StableHash(string value) => value.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)) & int.MaxValue;

    private static Result<FlightSearchResult, ProviderError> Failure(ProviderErrorKind kind, string message) =>
        Result<FlightSearchResult, ProviderError>.Failure(new ProviderError(kind, message));
}
