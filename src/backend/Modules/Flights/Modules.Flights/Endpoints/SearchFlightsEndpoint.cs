using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Endpoints;

internal static class SearchFlightsEndpoint
{
    // Invalid requests never reach here: built-in validation answers them with a 400 ValidationProblem.
    public static async Task<Results<Ok<FlightSearchResponse>, ValidationProblem, ProblemHttpResult>> Handle(
        FlightSearchRequest request,
        SearchFlightsHandler handler,
        IAirportDirectory airports,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCriteria(), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(FlightSearchResponse.From(result.Value, airports));
        }

        return result.Error switch
        {
            SearchFlightsFailure.InvalidDates invalid => TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { [invalid.Field] = [invalid.Message] }),
            SearchFlightsFailure.ProviderFailed failed => ProviderProblems.For(failed.Error.Kind),
            _ => throw new InvalidOperationException($"Unhandled search failure {result.Error}."),
        };
    }
}

/// <summary>
/// Maps the provider error taxonomy to HTTP problems for a search, which is a read (provider-integration.md).
/// Stable machine-readable types (api-design rules); never supplier details.
/// </summary>
internal static class ProviderProblems
{
    public static ProblemHttpResult For(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Unavailable or ProviderErrorKind.RateLimited or ProviderErrorKind.AuthFailure or ProviderErrorKind.Unknown =>
            Problem(StatusCodes.Status503ServiceUnavailable, "provider-unavailable", "Flight search is temporarily unavailable. Please try again shortly."),
        ProviderErrorKind.InvalidRequest =>
            Problem(StatusCodes.Status422UnprocessableEntity, "search-rejected", "The flight supplier could not process this search."),
        _ => Problem(StatusCodes.Status502BadGateway, "provider-error", "The flight supplier returned an unexpected response."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title) =>
        TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <summary>A search's offers, selectable by <see cref="SearchId"/> plus an offer id until the offers expire (Option 2).</summary>
/// <param name="Airports">The airports the offers use that are in the reference data (others are shown by code).</param>
internal sealed record FlightSearchResponse(Guid SearchId, IReadOnlyList<FlightOfferResponse> Offers, IReadOnlyList<AirportResponse> Airports)
{
    public static FlightSearchResponse From(CachedFlightSearch search, IAirportDirectory airports) => new(
        search.SearchId,
        search.Offers.Select(o => FlightOfferResponse.From(o.OfferId, o.Offer, airports)).ToList(),
        search.Offers
            .SelectMany(o => o.Offer.Slices.SelectMany(slice => slice.Segments.SelectMany(s => new[] { s.Origin, s.Destination })))
            .Distinct()
            .Select(airports.Find)
            .OfType<Airport>()
            .OrderBy(a => a.Code.Value, StringComparer.Ordinal)
            .Select(AirportResponse.From)
            .ToList());
}

/// <summary>
/// An offer as shown to the customer, with OUR opaque offer id: the adapter's offer token is never exposed.
/// </summary>
internal sealed record FlightOfferResponse(Guid OfferId, MoneyResponse TotalPrice, DateTimeOffset ExpiresAt, IReadOnlyList<FlightSliceResponse> Slices, FlightFareResponse Fare)
{
    public static FlightOfferResponse From(Guid offerId, FlightOffer offer, IAirportDirectory airports) => new(
        offerId,
        MoneyResponse.From(offer.TotalPrice),
        offer.ExpiresAt,
        FlightSliceResponse.From(offer.Slices, airports),
        FlightFareResponse.From(offer.Fare));
}

/// <summary>What the supplier states about the fare; "NotStated" and null mean it did not say.</summary>
internal sealed record FlightFareResponse(
    FlightPriceBreakdownResponse? PriceBreakdown,
    string? ValidatingCarrier,
    BaggageAllowanceResponse? Baggage,
    FareAllowance Refund,
    FareAllowance Change,
    DateTimeOffset? TicketingDeadline)
{
    public static FlightFareResponse From(FlightFare fare) => new(
        fare.PriceBreakdown is { } breakdown ? FlightPriceBreakdownResponse.From(breakdown) : null,
        fare.ValidatingCarrier,
        fare.Baggage is { } baggage ? new BaggageAllowanceResponse(baggage.CheckedBags, baggage.CabinBags, baggage.CheckedBagMaxWeightKg) : null,
        fare.Conditions.Refund,
        fare.Conditions.Change,
        fare.TicketingDeadline);
}

/// <param name="Passengers">Per passenger type; each fare is per passenger.</param>
internal sealed record FlightPriceBreakdownResponse(MoneyResponse BaseFare, MoneyResponse TaxesAndFees, IReadOnlyList<PassengerFareResponse> Passengers)
{
    public static FlightPriceBreakdownResponse From(FlightPriceBreakdown breakdown) => new(
        MoneyResponse.From(breakdown.BaseFare),
        MoneyResponse.From(breakdown.TaxesAndFees),
        breakdown.Passengers.Select(p => new PassengerFareResponse(p.Type, p.Count, MoneyResponse.From(p.BaseFare), MoneyResponse.From(p.TaxesAndFees))).ToList());
}

internal sealed record PassengerFareResponse(PassengerType Type, int Count, MoneyResponse BaseFare, MoneyResponse TaxesAndFees);

/// <param name="CheckedBagMaxWeightKg">Per checked bag, when the supplier states a limit.</param>
internal sealed record BaggageAllowanceResponse(int CheckedBags, int CabinBags, int? CheckedBagMaxWeightKg);

/// <param name="TimeZone">The IANA time zone the airport's local times are in.</param>
internal sealed record AirportResponse(string Code, string Name, string CityName, string CountryCode, string TimeZone)
{
    public static AirportResponse From(Airport airport) => new(airport.Code.Value, airport.Name, airport.CityName, airport.CountryCode, airport.TimeZoneId);
}

/// <summary>Money in JSON: the amount is a string to avoid float precision loss (api-design rules).</summary>
internal sealed record MoneyResponse(string Amount, string Currency)
{
    public static MoneyResponse From(Money money) => new(money.Amount.ToString(CultureInfo.InvariantCulture), money.Currency.Value);
}

internal sealed record FlightSliceResponse(IReadOnlyList<FlightSegmentResponse> Segments)
{
    public static IReadOnlyList<FlightSliceResponse> From(IReadOnlyList<FlightSlice> slices, IAirportDirectory airports) =>
        slices.Select(slice => new FlightSliceResponse(slice.Segments.Select(s => FlightSegmentResponse.From(s, airports)).ToList())).ToList();
}

/// <summary>
/// Departure and arrival are local times at the origin and destination airports (ADR 0010), with those airports' IANA time
/// zones when they are in the reference data (null otherwise). The flying time is the supplier's, or computed from the zones.
/// </summary>
internal sealed record FlightSegmentResponse(
    string MarketingCarrier,
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal,
    string? OperatingCarrier,
    int? DurationMinutes,
    string? OriginTimeZone,
    string? DestinationTimeZone)
{
    /// <param name="airports">Gives the flying time from the airports' time zones when the supplier does not state it.</param>
    public static FlightSegmentResponse From(FlightSegment segment, IAirportDirectory airports) => new(
        segment.MarketingCarrier,
        segment.FlightNumber,
        segment.Origin.Value,
        segment.Destination.Value,
        segment.DepartureLocal,
        segment.ArrivalLocal,
        segment.OperatingCarrier,
        airports.DurationOf(segment) is { } duration ? (int)duration.TotalMinutes : null,
        airports.Find(segment.Origin)?.TimeZoneId,
        airports.Find(segment.Destination)?.TimeZoneId);
}
