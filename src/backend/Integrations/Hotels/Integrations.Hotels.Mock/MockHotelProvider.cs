using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Integrations.Hotels.Mock;

/// <summary>
/// Destinations that choose the mock's behaviour (provider-integration.md: deterministic, scenario-driven). Any other
/// IATA city code returns three properties whose offers revalidate at the same price.
/// </summary>
public static class MockHotelScenarios
{
    /// <summary>Revalidation quotes a price 10% higher (F-01).</summary>
    public const string PriceChangedDestination = "ZPC";

    /// <summary>Revalidation finds the offer expired (F-02).</summary>
    public const string ExpiredDestination = "ZEX";

    /// <summary>Revalidation finds the room sold out (F-03).</summary>
    public const string SoldOutDestination = "ZSO";

    /// <summary>The search finds no availability.</summary>
    public const string NoAvailabilityDestination = "ZNA";

    /// <summary>The search fails as the supplier being unavailable.</summary>
    public const string UnavailableDestination = "ZUN";
}

/// <summary>
/// A deterministic hotel provider for development and tests (ADR 0004, ADR 0030): prepaid rates in the test currency
/// XTS, one refundable rate with a deadline and a penalty, one non-refundable rate, and one refundable rate with fees at
/// the property. Its offer token is its own (the core never parses it). No network, no randomness.
/// </summary>
public sealed class MockHotelProvider(TimeProvider timeProvider) : IHotelProvider
{
    public const string ProviderId = "mockhotels";

    /// <summary>How long a mock offer stays bookable.</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);

    private static readonly CurrencyCode _xts = new("XTS");

    private static readonly (string Name, decimal Stars, string Room, BoardBasis Board, decimal Nightly, string Kind)[] _rates =
    [
        ("Mock Central Hotel", 4m, "Double room", BoardBasis.Breakfast, 120m, "refundable-penalty"),
        ("Mock Riverside Inn", 3m, "Standard double room", BoardBasis.RoomOnly, 80m, "non-refundable"),
        ("Mock Grand Suites", 5m, "Junior suite", BoardBasis.HalfBoard, 210m, "refundable-fees"),
    ];

    public string Id => ProviderId;

    public Task<Result<HotelSearchResult, ProviderError>> SearchAsync(HotelSearchCriteria criteria, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!criteria.IsWellFormed())
        {
            return Task.FromResult(Result<HotelSearchResult, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "Malformed search.")));
        }

        if (criteria.Destination == MockHotelScenarios.UnavailableDestination)
        {
            return Task.FromResult(Result<HotelSearchResult, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Mock supplier unavailable (scenario).")));
        }

        IReadOnlyList<HotelOffer> offers = criteria.Destination == MockHotelScenarios.NoAvailabilityDestination
            ? []
            : [.. Enumerable.Range(0, _rates.Length).Select(index => Offer(criteria, index, priceFactor: 1m))];
        return Task.FromResult(Result<HotelSearchResult, ProviderError>.Success(new HotelSearchResult(offers)));
    }

    public Task<Result<HotelOffer, ProviderError>> RevalidateAsync(HotelOfferRef offer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offer.ProviderId != ProviderId || Parse(offer.Value) is not { } parsed)
        {
            return Task.FromResult(Result<HotelOffer, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "Unknown offer.")));
        }

        var (criteria, index) = parsed;
        return Task.FromResult(criteria.Destination switch
        {
            MockHotelScenarios.ExpiredDestination => Result<HotelOffer, ProviderError>.Failure(new ProviderError(ProviderErrorKind.OfferExpired, "Offer expired (scenario).")),
            MockHotelScenarios.SoldOutDestination => Result<HotelOffer, ProviderError>.Failure(new ProviderError(ProviderErrorKind.SoldOut, "Room sold out (scenario).")),
            MockHotelScenarios.PriceChangedDestination => Result<HotelOffer, ProviderError>.Success(Offer(criteria, index, priceFactor: 1.1m)),
            _ => Result<HotelOffer, ProviderError>.Success(Offer(criteria, index, priceFactor: 1m)),
        });
    }

    private HotelOffer Offer(HotelSearchCriteria criteria, int index, decimal priceFactor)
    {
        var rate = _rates[index];
        // Per night: the room for two, plus 25% per extra adult and 15 per child; rounded to the cent.
        var nightly = rate.Nightly * (1 + (0.25m * Math.Max(0, criteria.Adults - 2))) + (15m * criteria.ChildAges.Count);
        var total = new Money(decimal.Round(nightly * criteria.Nights * priceFactor, 2, MidpointRounding.ToEven), _xts);
        // Free cancellation ends two days before check-in, at noon UTC (the mock's properties keep UTC).
        var deadline = new DateTimeOffset(criteria.CheckIn.AddDays(-2).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var cancellation = rate.Kind switch
        {
            "non-refundable" => CancellationPolicy.NonRefundable,
            "refundable-penalty" => CancellationPolicy.FreeUntil(deadline, new Money(decimal.Round(nightly * priceFactor, 2, MidpointRounding.ToEven), _xts)),
            _ => CancellationPolicy.FreeUntil(deadline, null),
        };
        var fees = rate.Kind == "refundable-fees" ? new Money(2m * criteria.Nights, _xts) : (Money?)null;
        return new HotelOffer(
            new HotelOfferRef(ProviderId, Token(criteria, index)),
            new HotelProperty($"mock-{index + 1}", rate.Name, $"{index + 1} Mock Street", criteria.Destination, "ZZ", rate.Stars, "UTC"),
            rate.Room,
            rate.Board,
            total,
            fees,
            cancellation,
            timeProvider.GetUtcNow() + OfferLifetime);
    }

    private static string Token(HotelSearchCriteria c, int index) => string.Join('|',
        "v1", c.Destination, c.CheckIn.ToString("yyyyMMdd", CultureInfo.InvariantCulture), c.CheckOut.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        c.Adults.ToString(CultureInfo.InvariantCulture), string.Join('.', c.ChildAges), index.ToString(CultureInfo.InvariantCulture));

    private static (HotelSearchCriteria Criteria, int Index)? Parse(string token)
    {
        var parts = token.Split('|');
        if (parts.Length != 7 || parts[0] != "v1"
            || !DateOnly.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkIn)
            || !DateOnly.TryParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkOut)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var adults)
            || !int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= _rates.Length)
        {
            return null;
        }

        var ages = parts[5].Length == 0 ? [] : parts[5].Split('.').Select(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var age) ? age : -1).ToList();
        var criteria = new HotelSearchCriteria(parts[1], checkIn, checkOut, adults, ages);
        return criteria.IsWellFormed() ? (criteria, index) : null;
    }
}

public static class MockHotelProviderRegistration
{
    /// <summary>
    /// Registers the mock as the <see cref="IHotelProvider"/>. It runs only in Development or Staging (an allow-list, so
    /// no production-like environment can get it by name), checked at host startup.
    /// </summary>
    public static IServiceCollection AddMockHotelProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MockHotelProviderOptions>()
            .Bind(configuration.GetSection(MockHotelProviderOptions.SectionName))
            .Validate<IHostEnvironment>((_, environment) => environment.IsDevelopment() || environment.IsStaging(), "The mock hotel provider only runs in Development or Staging.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHotelProvider, MockHotelProvider>();
        return services;
    }
}

/// <summary><c>Integrations:Hotels:Mock</c>: nothing to set yet; it exists so startup checks the environment.</summary>
public sealed class MockHotelProviderOptions
{
    public const string SectionName = "Integrations:Hotels:Mock";
}
