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
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCriteria(), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(FlightSearchResponse.From(result.Value));
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
internal sealed record FlightSearchResponse(Guid SearchId, IReadOnlyList<FlightOfferResponse> Offers)
{
    public static FlightSearchResponse From(CachedFlightSearch search) =>
        new(search.SearchId, search.Offers.Select(o => FlightOfferResponse.From(o.OfferId, o.Offer)).ToList());
}

/// <summary>
/// An offer as shown to the customer, with OUR opaque offer id: the adapter's offer token is never exposed.
/// </summary>
internal sealed record FlightOfferResponse(Guid OfferId, MoneyResponse TotalPrice, DateTimeOffset ExpiresAt, IReadOnlyList<FlightSliceResponse> Slices)
{
    public static FlightOfferResponse From(Guid offerId, FlightOffer offer) => new(
        offerId,
        MoneyResponse.From(offer.TotalPrice),
        offer.ExpiresAt,
        FlightSliceResponse.From(offer.Slices));
}

/// <summary>Money in JSON: the amount is a string to avoid float precision loss (api-design rules).</summary>
internal sealed record MoneyResponse(string Amount, string Currency)
{
    public static MoneyResponse From(Money money) => new(money.Amount.ToString(CultureInfo.InvariantCulture), money.Currency.Value);
}

internal sealed record FlightSliceResponse(IReadOnlyList<FlightSegmentResponse> Segments)
{
    public static IReadOnlyList<FlightSliceResponse> From(IReadOnlyList<FlightSlice> slices) =>
        slices.Select(slice => new FlightSliceResponse(slice.Segments.Select(FlightSegmentResponse.From).ToList())).ToList();
}

/// <summary>Departure and arrival are local times at the origin and destination airports (ADR 0010).</summary>
internal sealed record FlightSegmentResponse(
    string MarketingCarrier,
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal)
{
    public static FlightSegmentResponse From(FlightSegment segment) => new(
        segment.MarketingCarrier,
        segment.FlightNumber,
        segment.Origin.Value,
        segment.Destination.Value,
        segment.DepartureLocal,
        segment.ArrivalLocal);
}
