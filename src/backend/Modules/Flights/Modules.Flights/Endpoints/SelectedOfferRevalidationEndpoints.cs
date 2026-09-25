using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.Endpoints;

internal static class SelectedOfferRevalidationEndpoints
{
    // 200: the supplier confirmed the agreed price. 422 price-changed: a new quote the customer must accept first.
    public static async Task<Results<Ok<ConfirmedFlightOfferResponse>, ProblemHttpResult>> Revalidate(
        Guid selectedOfferId,
        RevalidateSelectedOfferHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(selectedOfferId, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Ok(ConfirmedFlightOfferResponse.From(result.Value))
            : SelectedOfferProblems.For(result.Error);
    }

    public static async Task<Results<Ok<ConfirmedFlightOfferResponse>, ProblemHttpResult>> AcceptPrice(
        Guid selectedOfferId,
        AcceptFlightOfferPriceRequest request,
        AcceptSelectedOfferPriceHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(selectedOfferId, request.PriceQuoteId!.Value, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Ok(ConfirmedFlightOfferResponse.From(result.Value))
            : SelectedOfferProblems.For(result.Error);
    }
}

/// <summary>Supplier-neutral Problem Details (api-design rules): stable types, no supplier codes or payloads.</summary>
internal static class SelectedOfferProblems
{
    public static ProblemHttpResult For(SelectedOfferFailure failure) => failure switch
    {
        SelectedOfferFailure.PriceChanged changed => PriceChanged(changed.Offer),
        SelectedOfferFailure.NotFound => Problem(StatusCodes.Status404NotFound, "selected-offer-not-found", "This selection was not found. Please search again."),
        SelectedOfferFailure.OfferExpired => Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired", "This offer is no longer available. Please search again."),
        SelectedOfferFailure.SoldOut => Problem(StatusCodes.Status422UnprocessableEntity, "sold-out", "This flight is sold out. Please search again."),
        SelectedOfferFailure.StaleQuote => Problem(StatusCodes.Status409Conflict, "price-quote-stale", "The price has changed again. Please check the latest price."),
        SelectedOfferFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "This selection was updated at the same time. Please try again."),
        SelectedOfferFailure.ProviderFailed { Error.Kind: ProviderErrorKind.Unavailable or ProviderErrorKind.RateLimited or ProviderErrorKind.AuthFailure or ProviderErrorKind.Unknown } =>
            Problem(StatusCodes.Status503ServiceUnavailable, "provider-unavailable", "Price checks are temporarily unavailable. Please try again shortly."),
        SelectedOfferFailure.ProviderFailed => Problem(StatusCodes.Status502BadGateway, "provider-error", "The flight supplier returned an unexpected response."),
        _ => throw new InvalidOperationException($"Unhandled selected-offer failure {failure}."),
    };

    /// <summary>F-01: the previous (agreed) and new totals, and the quote to accept. Nothing is charged or changed silently.</summary>
    private static ProblemHttpResult PriceChanged(SelectedOffer offer) => TypedResults.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "price-changed",
        title: "The price of this flight has changed. Please review the new price.",
        extensions: new Dictionary<string, object?>
        {
            ["selectedOfferId"] = offer.Id,
            ["previousTotalPrice"] = MoneyResponse.From(offer.AgreedPrice),
            ["newTotalPrice"] = MoneyResponse.From(offer.QuotedPrice!.Value),
            ["priceQuoteId"] = offer.PriceQuoteId,
            ["offerExpiresAt"] = offer.OfferExpiresAt,
            ["requiresConfirmation"] = true,
        });

    private static ProblemHttpResult Problem(int status, string type, string title) =>
        TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <summary>
/// The selection once the supplier has confirmed the price and the customer has agreed to it: ready for a later
/// booking step. <see cref="SelectedTotalPrice"/> is what was originally selected, kept for comparison.
/// </summary>
internal sealed record ConfirmedFlightOfferResponse(
    Guid SelectedOfferId,
    MoneyResponse TotalPrice,
    MoneyResponse SelectedTotalPrice,
    DateTimeOffset OfferExpiresAt,
    DateTimeOffset RevalidatedAt)
{
    public static ConfirmedFlightOfferResponse From(SelectedOffer offer) => new(
        offer.Id,
        MoneyResponse.From(offer.AgreedPrice),
        MoneyResponse.From(offer.TotalPrice),
        offer.OfferExpiresAt,
        offer.RevalidatedAt ?? throw new InvalidOperationException("A confirmed offer has been revalidated."));
}

/// <summary>
/// The 422 body of revalidation and price acceptance (RFC 9457, api-design rules): <c>type</c> is
/// <c>price-changed</c> (F-01, with the price fields and <c>requiresConfirmation</c>), <c>offer-expired</c> (F-02),
/// or <c>sold-out</c> (F-03). Documents the Problem Details extensions for the generated client.
/// </summary>
internal sealed class SelectedOfferProblemResponse
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public string? TraceId { get; init; }

    // Present only for price-changed.
    public Guid? SelectedOfferId { get; init; }

    public MoneyResponse? PreviousTotalPrice { get; init; }

    public MoneyResponse? NewTotalPrice { get; init; }

    public Guid? PriceQuoteId { get; init; }

    public DateTimeOffset? OfferExpiresAt { get; init; }

    public bool? RequiresConfirmation { get; init; }
}
