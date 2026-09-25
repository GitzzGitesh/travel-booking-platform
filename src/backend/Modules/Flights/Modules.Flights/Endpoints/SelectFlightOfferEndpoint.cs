using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.Endpoints;

internal static class SelectFlightOfferEndpoint
{
    // 201 when this request selected the offer; 200 when it was already selected (idempotent replay).
    public static async Task<Results<Created<SelectedFlightOfferResponse>, Ok<SelectedFlightOfferResponse>, ProblemHttpResult>> Handle(
        SelectFlightOfferRequest request,
        SelectFlightOfferHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.SearchId!.Value, request.OfferId!.Value, cancellationToken);

        if (!result.IsSuccess)
        {
            // F-02 (failure-scenarios.md): the search or offer is no longer available; the customer searches again.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: "offer-expired",
                title: "This offer is no longer available. Please search again.");
        }

        var response = SelectedFlightOfferResponse.From(result.Value.Offer);
        return result.Value.Created ? TypedResults.Created((string?)null, response) : TypedResults.Ok(response);
    }
}

/// <summary>
/// The stored selection. It is not a booking or a price guarantee: the price and availability are revalidated with the
/// supplier before booking. The adapter's offer token is never exposed.
/// </summary>
internal sealed record SelectedFlightOfferResponse(
    Guid SelectedOfferId,
    Guid SearchId,
    Guid OfferId,
    MoneyResponse TotalPrice,
    DateTimeOffset OfferExpiresAt,
    IReadOnlyList<FlightSliceResponse> Slices)
{
    public static SelectedFlightOfferResponse From(SelectedOffer offer) => new(
        offer.Id,
        offer.SearchId,
        offer.OfferId,
        MoneyResponse.From(offer.TotalPrice),
        offer.OfferExpiresAt,
        FlightSliceResponse.From(offer.Slices));
}
