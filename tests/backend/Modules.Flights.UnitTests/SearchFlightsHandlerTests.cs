using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Endpoints;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

public sealed class SearchFlightsHandlerTests
{
    private static readonly FakeTimeProvider _clock = new(new DateTimeOffset(2027, 3, 10, 0, 30, 0, TimeSpan.Zero));
    private static readonly DateOnly _todayUtc = new(2027, 3, 10);

    [Fact]
    public async Task Yesterday_in_utc_is_still_searchable_because_local_dates_lag_utc()
    {
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])));

        var result = await Handler(provider).HandleAsync(Criteria(_todayUtc.AddDays(-1)), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Earlier_departures_are_rejected_without_calling_the_provider()
    {
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])));

        var result = await Handler(provider).HandleAsync(Criteria(_todayUtc.AddDays(-2)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(new SearchFlightsFailure.InvalidDates("DepartureDate", "The departure date is in the past."));
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_last_day_of_the_sales_horizon_is_searchable()
    {
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])));

        var result = await Handler(provider).HandleAsync(Criteria(_todayUtc.AddDays(SearchFlightsHandler.SalesHorizonDays)), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Departures_beyond_the_sales_horizon_are_rejected_without_calling_the_provider()
    {
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])));

        var result = await Handler(provider).HandleAsync(Criteria(_todayUtc.AddDays(SearchFlightsHandler.SalesHorizonDays + 1)), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SearchFlightsFailure.InvalidDates>().Field.ShouldBe("DepartureDate");
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Returns_beyond_the_sales_horizon_are_rejected()
    {
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Success(new FlightSearchResult([])));
        var criteria = new FlightSearchCriteria(new AirportCode("LHR"), new AirportCode("JFK"), _todayUtc.AddDays(300), _todayUtc.AddDays(SearchFlightsHandler.SalesHorizonDays + 1), new PassengerMix(1), CabinClass.Economy);

        var result = await Handler(provider).HandleAsync(criteria, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<SearchFlightsFailure.InvalidDates>().Field.ShouldBe("ReturnDate");
    }

    [Fact]
    public async Task Provider_errors_are_passed_through_unchanged()
    {
        var error = new ProviderError(ProviderErrorKind.Unavailable, "down");
        var provider = new StubProvider(Result<FlightSearchResult, ProviderError>.Failure(error));

        var result = await Handler(provider).HandleAsync(Criteria(_todayUtc.AddDays(30)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(new SearchFlightsFailure.ProviderFailed(error));
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unavailable, StatusCodes.Status503ServiceUnavailable, "provider-unavailable")]
    [InlineData(ProviderErrorKind.RateLimited, StatusCodes.Status503ServiceUnavailable, "provider-unavailable")]
    [InlineData(ProviderErrorKind.AuthFailure, StatusCodes.Status503ServiceUnavailable, "provider-unavailable")]
    [InlineData(ProviderErrorKind.Unknown, StatusCodes.Status503ServiceUnavailable, "provider-unavailable")]
    [InlineData(ProviderErrorKind.InvalidRequest, StatusCodes.Status422UnprocessableEntity, "search-rejected")]
    [InlineData(ProviderErrorKind.PriceChanged, StatusCodes.Status502BadGateway, "provider-error")]
    [InlineData(ProviderErrorKind.OfferExpired, StatusCodes.Status502BadGateway, "provider-error")]
    [InlineData(ProviderErrorKind.SoldOut, StatusCodes.Status502BadGateway, "provider-error")]
    [InlineData(ProviderErrorKind.Rejected, StatusCodes.Status502BadGateway, "provider-error")]
    public void Every_provider_error_maps_to_a_stable_problem(ProviderErrorKind kind, int status, string type)
    {
        var problem = ProviderProblems.For(kind).ProblemDetails;

        problem.Status.ShouldBe(status);
        problem.Type.ShouldBe(type);
    }

    [Fact]
    public void The_mapping_covers_the_whole_taxonomy() =>
        Enum.GetValues<ProviderErrorKind>().ShouldAllBe(kind => ProviderProblems.For(kind).StatusCode >= 422);

    private static SearchFlightsHandler Handler(IFlightProvider provider) =>
        new(provider, new FlightSearchCache(NewCache(), NullLogger<FlightSearchCache>.Instance), _clock, NullLogger<SearchFlightsHandler>.Instance);

    internal static HybridCache NewCache() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    private static FlightSearchCriteria Criteria(DateOnly departure) =>
        new(new AirportCode("LHR"), new AirportCode("JFK"), departure, null, new PassengerMix(1), CabinClass.Economy);

    private sealed class StubProvider(Result<FlightSearchResult, ProviderError> result) : IFlightProvider
    {
        public int Calls { get; private set; }

        public string Id => "stub";

        public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
