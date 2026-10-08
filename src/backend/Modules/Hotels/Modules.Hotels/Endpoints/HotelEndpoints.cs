using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Application;
using TravelBooking.Modules.Hotels.Domain;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.Endpoints;

/// <summary>
/// A hotel search (ADR 0030): one room at launch. Public because the validation source generator skips internal types
/// (ADR 0003); the rules mirror <see cref="HotelSearchCriteria"/>, so an invalid search is a 400 at the boundary.
/// </summary>
public sealed class HotelSearchRequest : IValidatableObject
{
    /// <summary>IATA city code, for example PAR.</summary>
    [Required]
    [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Must be a three-letter upper-case IATA city code.")]
    public string? Destination { get; init; }

    /// <summary>The property's local check-in date.</summary>
    [Required]
    public DateOnly? CheckIn { get; init; }

    /// <summary>The property's local check-out date: 1 to 30 nights after check-in.</summary>
    [Required]
    public DateOnly? CheckOut { get; init; }

    [Range(1, HotelSearchCriteria.MaxAdults)]
    public int Adults { get; init; } = 2;

    /// <summary>Each child's age at check-out (0 to 17), as their date of birth will show; at most three children.</summary>
    [MaxLength(HotelSearchCriteria.MaxChildren)]
    public List<int>? ChildAges { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CheckOut <= CheckIn)
        {
            yield return new ValidationResult("The check-out date must be after the check-in date.", [nameof(CheckOut)]);
        }

        if (ChildAges?.Any(age => age is < 0 or > HotelSearchCriteria.MaxChildAge) == true)
        {
            yield return new ValidationResult($"Each child's age is 0 to {HotelSearchCriteria.MaxChildAge}.", [nameof(ChildAges)]);
        }
    }

    // Only called after validation passed.
    internal HotelSearchCriteria ToCriteria() => new(Destination!, CheckIn!.Value, CheckOut!.Value, Adults, [.. ChildAges ?? []]);
}

/// <summary>The offer of a search to select, by the ids the search returned (never a price: ADR 0030, booking rules).</summary>
public sealed class SelectHotelOfferRequest
{
    [Required]
    public Guid? SearchId { get; init; }

    [Required]
    public Guid? OfferId { get; init; }
}

/// <summary>Accepts a changed price by the quote id the customer was shown (F-01).</summary>
public sealed class AcceptHotelPriceRequest
{
    [Required]
    public Guid? PriceQuoteId { get; init; }
}

/// <summary>Hotel search, selection and price check (ADR 0030). Thin: one handler call each.</summary>
internal static class HotelEndpoints
{
    public static async Task<Results<Ok<HotelSearchResponse>, ValidationProblem, ProblemHttpResult>> Search(
        HotelSearchRequest request, SearchHotelsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCriteria(), cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(HotelSearchResponse.From(result.Value));
        }

        return result.Error switch
        {
            SearchHotelsFailure.InvalidDates invalid => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [invalid.Field] = [invalid.Message] }),
            SearchHotelsFailure.ProviderFailed { Error.Kind: ProviderErrorKind.Unavailable or ProviderErrorKind.RateLimited or ProviderErrorKind.AuthFailure or ProviderErrorKind.Unknown } =>
                Problem(StatusCodes.Status503ServiceUnavailable, "provider-unavailable", "Hotel search is temporarily unavailable. Please try again shortly."),
            SearchHotelsFailure.ProviderFailed { Error.Kind: ProviderErrorKind.InvalidRequest or ProviderErrorKind.Rejected } =>
                Problem(StatusCodes.Status422UnprocessableEntity, "search-rejected", "The hotel supplier could not process this search."),
            _ => Problem(StatusCodes.Status502BadGateway, "provider-error", "The hotel supplier returned an unexpected response."),
        };
    }

    // Anonymous, or a signed-in customer (token or session) who then owns the selection; a refused credential is a 401.
    public static async Task<Results<Created<SelectedHotelOfferResponse>, Ok<SelectedHotelOfferResponse>, ProblemHttpResult>> Select(
        SelectHotelOfferRequest request, SelectHotelOfferHandler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = await http.AuthenticateOptionalCustomerAsync();
        if (caller.IsRejected)
        {
            return Unauthorized();
        }

        var result = await handler.HandleAsync(request.SearchId!.Value, request.OfferId!.Value, caller.CustomerId, cancellationToken);
        if (result is null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired", "This offer is no longer available. Please search again.");
        }

        var body = SelectedHotelOfferResponse.From(result.Selection);
        return result.Created ? TypedResults.Created($"/api/v1/hotels/selected-offers/{result.Selection.Id}", body) : TypedResults.Ok(body);
    }

    // 200: the supplier confirmed the agreed price. 422 price-changed: a new quote the customer must accept first.
    public static async Task<Results<Ok<ConfirmedHotelOfferResponse>, ProblemHttpResult>> Revalidate(
        Guid selectedOfferId, RevalidateHotelSelectionHandler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = await http.AuthenticateOptionalCustomerAsync();
        if (caller.IsRejected)
        {
            return Unauthorized();
        }

        var result = await handler.HandleAsync(selectedOfferId, caller.CustomerId, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(ConfirmedHotelOfferResponse.From(result.Value)) : For(result.Error);
    }

    public static async Task<Results<Ok<ConfirmedHotelOfferResponse>, ProblemHttpResult>> AcceptPrice(
        Guid selectedOfferId, AcceptHotelPriceRequest request, AcceptHotelPriceHandler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var caller = await http.AuthenticateOptionalCustomerAsync();
        if (caller.IsRejected)
        {
            return Unauthorized();
        }

        var result = await handler.HandleAsync(selectedOfferId, request.PriceQuoteId!.Value, caller.CustomerId, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(ConfirmedHotelOfferResponse.From(result.Value)) : For(result.Error);
    }

    private static ProblemHttpResult For(HotelSelectionFailure failure) => failure switch
    {
        HotelSelectionFailure.PriceChanged changed => TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            type: "price-changed",
            title: changed.Selection.TermsChanged
                ? "The price or the terms of this stay have changed. Please review them."
                : "The price of this stay has changed. Please review the new price.",
            extensions: new Dictionary<string, object?>
            {
                ["selectedOfferId"] = changed.Selection.Id,
                ["previousTotalPrice"] = HotelAmountResponse.From(changed.Selection.AgreedPrice),
                ["newTotalPrice"] = HotelAmountResponse.From(changed.Selection.QuotedPrice!.Value),
                ["priceQuoteId"] = changed.Selection.PriceQuoteId,
                ["offerExpiresAt"] = changed.Selection.OfferExpiresAt,
                ["requiresConfirmation"] = true,
                ["termsChanged"] = changed.Selection.TermsChanged,
                ["cancellation"] = HotelCancellationResponse.From(changed.Selection.Cancellation),
                ["room"] = changed.Selection.RoomDescription,
                ["board"] = changed.Selection.Board.ToString(),
            }),
        HotelSelectionFailure.NotFound => Problem(StatusCodes.Status404NotFound, "selected-offer-not-found", "This selection was not found. Please search again."),
        HotelSelectionFailure.OfferExpired => Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired", "This offer is no longer available. Please search again."),
        HotelSelectionFailure.SoldOut => Problem(StatusCodes.Status422UnprocessableEntity, "sold-out", "This room is no longer available. Please search again."),
        HotelSelectionFailure.StaleQuote => Problem(StatusCodes.Status409Conflict, "price-quote-stale", "The price has changed again. Please check the latest price."),
        HotelSelectionFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "This selection was updated at the same time. Please try again."),
        HotelSelectionFailure.Booked => Problem(StatusCodes.Status409Conflict, "selection-booked", "This stay is already booked. See it in My trips."),
        HotelSelectionFailure.ProviderFailed { Error.Kind: ProviderErrorKind.Unavailable or ProviderErrorKind.RateLimited or ProviderErrorKind.AuthFailure or ProviderErrorKind.Unknown } =>
            Problem(StatusCodes.Status503ServiceUnavailable, "provider-unavailable", "Price checks are temporarily unavailable. Please try again shortly."),
        _ => Problem(StatusCodes.Status502BadGateway, "provider-error", "The hotel supplier returned an unexpected response."),
    };

    private static ProblemHttpResult Unauthorized() =>
        Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Your session is not valid. Please sign in again.");

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <summary>An amount as a decimal string with its ISO-4217 currency (api-design rules: never a JSON number).</summary>
internal sealed record HotelAmountResponse(string Amount, string Currency)
{
    // The same amount always reads the same, as searched or as stored (decimal(19,4)): 2 to 4 decimals, never trailing noise.
    public static HotelAmountResponse From(Money money) => new(money.Amount.ToString("0.00##", CultureInfo.InvariantCulture), money.Currency.Value);
}

/// <summary>The property as shown to customers: never the supplier's own id.</summary>
internal sealed record HotelPropertyResponse(string Name, string AddressLine, string CityCode, string CountryCode, decimal? StarRating, string TimeZone);

/// <param name="FreeCancellationUntil">Until this instant, cancelling costs nothing (show it in <see cref="HotelPropertyResponse.TimeZone"/>).</param>
/// <param name="PenaltyAfterDeadline">Charged when cancelling after the deadline; null with a deadline means the whole price.</param>
internal sealed record HotelCancellationResponse(bool Refundable, DateTimeOffset? FreeCancellationUntil, HotelAmountResponse? PenaltyAfterDeadline)
{
    public static HotelCancellationResponse From(CancellationPolicy policy) =>
        new(policy.Refundable, policy.FreeCancellationUntil, policy.PenaltyAfterDeadline is { } p ? HotelAmountResponse.From(p) : null);
}

/// <param name="TotalPrice">The total for the stay, payable now (taxes and fees we collect included).</param>
/// <param name="FeesAtProperty">Fees the property collects itself (for example local taxes): information only, never charged by us.</param>
internal sealed record HotelOfferResponse(
    Guid OfferId, HotelPropertyResponse Property, string Room, BoardBasis Board, HotelAmountResponse TotalPrice, HotelAmountResponse? FeesAtProperty,
    HotelCancellationResponse Cancellation, DateTimeOffset ExpiresAt);

internal sealed record HotelSearchResponse(Guid SearchId, int Nights, IReadOnlyList<HotelOfferResponse> Offers)
{
    public static HotelSearchResponse From(CachedHotelSearch search) => new(
        search.SearchId,
        search.Criteria.Nights,
        [.. search.Offers.Select(o => new HotelOfferResponse(
            o.OfferId,
            new HotelPropertyResponse(o.Offer.Property.Name, o.Offer.Property.AddressLine, o.Offer.Property.CityCode, o.Offer.Property.CountryCode,
                o.Offer.Property.StarRating, o.Offer.Property.TimeZone),
            o.Offer.RoomDescription,
            o.Offer.Board,
            HotelAmountResponse.From(o.Offer.TotalPrice),
            o.Offer.FeesAtProperty is { } fees ? HotelAmountResponse.From(fees) : null,
            HotelCancellationResponse.From(o.Offer.Cancellation),
            o.Offer.ExpiresAt))]);
}

/// <summary>The stored selection: what the customer chose, at the price selected (it is checked again before booking).</summary>
internal sealed record SelectedHotelOfferResponse(
    Guid SelectedOfferId, Guid SearchId, Guid OfferId, HotelPropertyResponse Property, string Room, BoardBasis Board, DateOnly CheckIn, DateOnly CheckOut,
    int Nights, HotelAmountResponse TotalPrice, HotelCancellationResponse Cancellation, DateTimeOffset OfferExpiresAt)
{
    public static SelectedHotelOfferResponse From(HotelSelection s) => new(
        s.Id, s.SearchId, s.OfferId,
        new HotelPropertyResponse(s.PropertyName, s.AddressLine, s.CityCode, s.CountryCode, s.StarRating, s.TimeZone),
        s.RoomDescription, s.Board, s.CheckIn, s.CheckOut, s.Nights, HotelAmountResponse.From(s.AgreedPrice), HotelCancellationResponse.From(s.Cancellation),
        s.OfferExpiresAt);
}

/// <summary>The selection once the supplier confirmed the price and the customer agreed to it: ready for booking (H2).</summary>
/// <param name="Room">The room confirmed (and, after a quote, accepted): what the customer books.</param>
/// <param name="Board">The board confirmed (and, after a quote, accepted).</param>
internal sealed record ConfirmedHotelOfferResponse(
    Guid SelectedOfferId, HotelAmountResponse TotalPrice, HotelAmountResponse SelectedTotalPrice, HotelCancellationResponse Cancellation,
    DateTimeOffset OfferExpiresAt, DateTimeOffset RevalidatedAt, string Room, BoardBasis Board)
{
    public static ConfirmedHotelOfferResponse From(HotelSelection s) => new(
        s.Id, HotelAmountResponse.From(s.AgreedPrice), HotelAmountResponse.From(s.TotalPrice), HotelCancellationResponse.From(s.Cancellation),
        s.OfferExpiresAt, s.RevalidatedAt ?? throw new InvalidOperationException("A confirmed selection has been revalidated."),
        s.RoomDescription, s.Board);
}

/// <summary>
/// The 422 body of revalidation and price acceptance (RFC 9457): <c>price-changed</c> (F-01, with the price fields),
/// <c>offer-expired</c> (F-02) or <c>sold-out</c> (F-03). Documents the Problem Details extensions for the client.
/// </summary>
internal sealed class HotelSelectionProblemResponse
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public string? TraceId { get; init; }

    public Guid? SelectedOfferId { get; init; }

    public HotelAmountResponse? PreviousTotalPrice { get; init; }

    public HotelAmountResponse? NewTotalPrice { get; init; }

    public Guid? PriceQuoteId { get; init; }

    public DateTimeOffset? OfferExpiresAt { get; init; }

    public bool? RequiresConfirmation { get; init; }

    /// <summary>The room, board or cancellation terms changed too (not only the price): show them before acceptance.</summary>
    public bool? TermsChanged { get; init; }

    /// <summary>The cancellation terms the customer is asked to accept.</summary>
    public HotelCancellationResponse? Cancellation { get; init; }

    /// <summary>The room the customer is asked to accept (it may differ from the one selected, F-53).</summary>
    public string? Room { get; init; }

    /// <summary>The board the customer is asked to accept (it may differ from the one selected, F-53).</summary>
    public BoardBasis? Board { get; init; }
}
