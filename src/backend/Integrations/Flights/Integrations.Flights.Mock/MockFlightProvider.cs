using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Mock;

/// <summary>
/// Deterministic flight offers: the same criteria and clock always give the same offers, with no randomness.
/// Prices use XTS, the ISO 4217 code reserved for testing, so no charge currency is implied (Q5 is open).
/// The fictional carrier ZZ and fixed timetable are not real schedules; arrival times ignore time-zone differences.
/// Revalidation outcomes are chosen per offer by reserved test destinations (provider-integration.md: magic values),
/// so one running Api can show every scenario; any other destination revalidates unchanged.
/// </summary>
internal sealed partial class MockFlightProvider(IOptions<MockFlightProviderOptions> options, TimeProvider timeProvider) : IFlightProvider
{
    public const string ProviderId = "mock";
    private const string _carrier = "ZZ";

    // The codeshare partner that operates the middle departure of each day (a marketing/operating carrier difference).
    private const string _operatingPartner = "ZY";
    private static readonly TimeSpan _ticketingWindow = TimeSpan.FromHours(24);
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

    public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ConfiguredFailure() is { } failure)
        {
            return Task.FromResult(Result<FlightOffer, ProviderError>.Failure(failure));
        }

        var current = Reprice(offer, out _);
        return Task.FromResult(current);
    }

    public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // "Unavailable" here means the details certainly was not processed (circuit open): nothing is booked.
        if (ConfiguredFailure() is { } failure)
        {
            return Task.FromResult(BookingFailure(failure.Kind, failure.Message));
        }

        // Idempotent by our reference, as a supplier with client-reference support is: never a second booking.
        if (_bookings.TryGetValue(details.ClientReference.Value, out var existing))
        {
            return Task.FromResult(Result<FlightBookingConfirmation, ProviderError>.Success(existing));
        }

        var current = Reprice(details.Offer, out var criteria);
        if (!current.IsSuccess)
        {
            return Task.FromResult(BookingFailure(current.Error.Kind, current.Error.Message));
        }

        if (!MatchesPassengerMix(details.Passengers, criteria!.Passengers))
        {
            return Task.FromResult(BookingFailure(ProviderErrorKind.InvalidRequest, "Passengers do not match the offer's passenger mix."));
        }

        if (current.Value.TotalPrice != details.ExpectedTotalPrice)
        {
            return Task.FromResult(BookingFailure(ProviderErrorKind.PriceChanged, "Mock offer price differs from the expected price."));
        }

        var confirmation = new FlightBookingConfirmation(details.ClientReference, new ProviderBookingRef(ProviderId, Locator(details.ClientReference)), current.Value.TotalPrice);
        var scenario = details.Passengers.Select(p => p.FamilyName.ToUpperInvariant()).FirstOrDefault(MockBookingScenarios.All.Contains);
        var result = scenario switch
        {
            MockBookingScenarios.RejectedFamilyName => BookingFailure(ProviderErrorKind.Rejected, "Mock booking rejected (scenario)."),
            MockBookingScenarios.TimeoutNotBookedFamilyName => BookingFailure(ProviderErrorKind.Unknown, "Mock booking timed out; nothing was booked (scenario)."),
            MockBookingScenarios.TimeoutBookedFamilyName => Store(confirmation, BookingFailure(ProviderErrorKind.Unknown, "Mock booking timed out after booking (scenario).")),
            _ => Store(confirmation, Result<FlightBookingConfirmation, ProviderError>.Success(confirmation)),
        };

        return Task.FromResult(result);
    }

    public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ConfiguredFailure() is { } failure)
        {
            return Task.FromResult(Result<FlightBookingLookup, ProviderError>.Failure(failure));
        }

        _bookings.TryGetValue(clientReference.Value, out var booking);
        return Task.FromResult(Result<FlightBookingLookup, ProviderError>.Success(new FlightBookingLookup(booking)));
    }

    // Bookings made by this mock, by our reference. In memory: they last as long as the process (Development only).
    private readonly ConcurrentDictionary<string, FlightBookingConfirmation> _bookings = new(StringComparer.Ordinal);

    // One booking per reference even under parallel requests: the first stored booking wins and is returned.
    private Result<FlightBookingConfirmation, ProviderError> Store(FlightBookingConfirmation confirmation, Result<FlightBookingConfirmation, ProviderError> result)
    {
        var stored = _bookings.GetOrAdd(confirmation.ClientReference.Value, confirmation);
        return result.IsSuccess ? Result<FlightBookingConfirmation, ProviderError>.Success(stored) : result;
    }

    private ProviderError? ConfiguredFailure() => options.Value.Scenario switch
    {
        MockFlightScenario.Unavailable => new ProviderError(ProviderErrorKind.Unavailable, "Mock provider unavailable (scenario)."),
        MockFlightScenario.RateLimited => new ProviderError(ProviderErrorKind.RateLimited, "Mock provider rate limited (scenario)."),
        _ => null,
    };

    /// <summary>
    /// Stateless: the reference carries everything that prices the offer, so it is repriced exactly as searched; the
    /// reserved destinations then apply the revalidation scenarios (which also hold at booking time).
    /// </summary>
    private Result<FlightOffer, ProviderError> Reprice(ProviderOfferRef offer, out FlightSearchCriteria? criteria)
    {
        criteria = null;
        if (offer.ProviderId != ProviderId || !TryParseReference(offer.Value, out var index, out var parsed))
        {
            return RevalidationFailure(ProviderErrorKind.InvalidRequest, "Unrecognised mock offer reference.");
        }

        criteria = parsed;
        var current = CreateOffers(parsed)[index];
        return parsed.Destination.Value switch
        {
            MockRevalidationScenarios.OfferExpiredDestination => RevalidationFailure(ProviderErrorKind.OfferExpired, "Mock offer expired (scenario)."),
            MockRevalidationScenarios.SoldOutDestination => RevalidationFailure(ProviderErrorKind.SoldOut, "Mock offer sold out (scenario)."),
            MockRevalidationScenarios.PriceChangedDestination => Result<FlightOffer, ProviderError>.Success(Repriced(current, MockRevalidationScenarios.PriceChangeFactor)),
            _ => Result<FlightOffer, ProviderError>.Success(current),
        };
    }

    private static bool MatchesPassengerMix(IReadOnlyList<FlightPassenger> passengers, PassengerMix mix) =>
        passengers.Count(p => p.Type == PassengerType.Adult) == mix.Adults
        && passengers.Count(p => p.Type == PassengerType.Child) == mix.Children
        && passengers.Count(p => p.Type == PassengerType.Infant) == mix.Infants;

    // A deterministic six-character locator, like a PNR: the same reference always gets the same locator.
    private static string Locator(ClientReference reference)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var seed = (uint)StableHash(reference.Value);
        return string.Concat(Enumerable.Range(0, 6).Select(i => alphabet[(int)((seed >> (i * 5)) % (uint)alphabet.Length)]));
    }

    private static Result<FlightBookingConfirmation, ProviderError> BookingFailure(ProviderErrorKind kind, string message) =>
        Result<FlightBookingConfirmation, ProviderError>.Failure(new ProviderError(kind, message));

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
            var breakdown = Breakdown(passengers, fare);

            // Everything that determines the price is in the reference, so a later revalidate can reprice it statelessly.
            var reference = new ProviderOfferRef(
                ProviderId,
                $"mock-{index}-{criteria.Origin}{criteria.Destination}-{criteria.DepartureDate:yyyyMMdd}-{criteria.ReturnDate:yyyyMMdd}-{criteria.Cabin}-{passengers.Adults}A{passengers.Children}C{passengers.Infants}I");

            return new FlightOffer(reference, breakdown.Total, expiresAt, slices)
            {
                Fare = new FlightFare
                {
                    PriceBreakdown = breakdown,
                    ValidatingCarrier = _carrier,
                    Baggage = _baggage[index],
                    Conditions = _conditions[index],
                    TicketingDeadline = timeProvider.GetUtcNow() + _ticketingWindow,
                },
            };
        }).ToList();
    }

    // Per departure of the day: a basic fare, a standard fare and a flexible fare.
    private static readonly BaggageAllowance[] _baggage = [new(0, 1), new(1, 1, 23), new(2, 1, 23)];

    private static readonly FareConditions[] _conditions =
    [
        new(FareAllowance.NotAllowed, FareAllowance.AllowedWithFee),
        new(FareAllowance.AllowedWithFee, FareAllowance.Free),
        new(FareAllowance.Free, FareAllowance.Free),
    ];

    // Adults and children pay the seat fare, infants a tenth of it; taxes and fees are 15% of each (rounded to the cent).
    private static FlightPriceBreakdown Breakdown(PassengerMix passengers, decimal seatFare)
    {
        var fares = new List<PassengerFare> { Fare(PassengerType.Adult, passengers.Adults, seatFare) };
        if (passengers.Children > 0)
        {
            fares.Add(Fare(PassengerType.Child, passengers.Children, seatFare));
        }

        if (passengers.Infants > 0)
        {
            fares.Add(Fare(PassengerType.Infant, passengers.Infants, seatFare * 0.1m));
        }

        return new FlightPriceBreakdown(fares);
    }

    private static PassengerFare Fare(PassengerType type, int count, decimal perPassenger)
    {
        var taxes = decimal.Round(perPassenger * 0.15m, 2);
        return new PassengerFare(type, count, new Money(perPassenger - taxes, _testCurrency), new Money(taxes, _testCurrency));
    }

    // A changed price, as a supplier would quote it: every passenger's fare rescaled, and the total their sum.
    private static FlightOffer Repriced(FlightOffer offer, decimal factor)
    {
        var breakdown = new FlightPriceBreakdown([.. offer.Fare.PriceBreakdown!.Passengers.Select(p => new PassengerFare(
            p.Type,
            p.Count,
            new Money(decimal.Round(p.BaseFare.Amount * factor, 2), p.BaseFare.Currency),
            new Money(decimal.Round(p.TaxesAndFees.Amount * factor, 2), p.TaxesAndFees.Currency)))]);
        return new FlightOffer(offer.Reference, breakdown.Total, offer.ExpiresAt, offer.Slices) { Fare = offer.Fare with { PriceBreakdown = breakdown } };
    }

    private FlightSlice Slice(AirportCode from, AirportCode to, DateOnly date, TimeOnly time, int routeSeed, int index)
    {
        var departure = date.ToDateTime(time, DateTimeKind.Unspecified);
        var flightNumber = $"{_carrier}{100 + ((routeSeed + (index * 7)) % 900)}";
        // Arrival is departure plus the flying time on the same clock: a mock simplification. It states its flying time,
        // so displayed durations are right; a real supplier gives both local times correctly.
        return new FlightSlice([new FlightSegment(_carrier, flightNumber, from, to, departure, departure + _flightDuration)
        {
            OperatingCarrier = index == 1 ? _operatingPartner : null,
            Duration = _flightDuration,
            FareBasis = $"{"BMF"[index]}{index}MOCK",
        }]);
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

    private static bool TryParseReference(string value, out int index, out FlightSearchCriteria criteria)
    {
        index = 0;
        criteria = null!;
        var match = ReferencePattern().Match(value);
        if (!match.Success || !Enum.TryParse<CabinClass>(match.Groups["cabin"].Value, out var cabin))
        {
            return false;
        }

        try
        {
            index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            criteria = new FlightSearchCriteria(
                new AirportCode(match.Groups["from"].Value),
                new AirportCode(match.Groups["to"].Value),
                ParseDate(match.Groups["out"].Value),
                match.Groups["back"].Success ? ParseDate(match.Groups["back"].Value) : null,
                new PassengerMix(Count(match, "a"), Count(match, "c"), Count(match, "i")),
                cabin);
            return index < _departureTimes.Length;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);

    private static int Count(Match match, string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    // The reference format written by CreateOffers.
    [GeneratedRegex(@"^mock-(?<index>\d)-(?<from>[A-Z]{3})(?<to>[A-Z]{3})-(?<out>\d{8})-(?<back>\d{8})?-(?<cabin>[A-Za-z]+)-(?<a>\d)A(?<c>\d)C(?<i>\d)I$")]
    private static partial Regex ReferencePattern();

    private static Result<FlightOffer, ProviderError> RevalidationFailure(ProviderErrorKind kind, string message) =>
        Result<FlightOffer, ProviderError>.Failure(new ProviderError(kind, message));

    private static Result<FlightSearchResult, ProviderError> Failure(ProviderErrorKind kind, string message) =>
        Result<FlightSearchResult, ProviderError>.Failure(new ProviderError(kind, message));
}
