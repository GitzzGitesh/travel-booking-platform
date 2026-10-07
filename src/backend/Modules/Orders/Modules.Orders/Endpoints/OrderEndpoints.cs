using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Endpoints;

/// <summary>
/// Creates the signed-in customer's order for a flight or hotel selection that was revalidated (and, if its price
/// changed, accepted). Only the selection id is sent: the price comes from Flights or Hotels, never from the client.
/// Public because the .NET 10 validation source generator skips internal types (ADR 0003). The name keeps "Flight"
/// deliberately: it is the v1 contract's schema name, and renaming it would break generated clients within v1.
/// </summary>
public sealed class CreateFlightOrderRequest
{
    [Required]
    public Guid? SelectedOfferId { get; init; }

    /// <summary>What the selection is: "Flight" (the default) or "Hotel" (ADR 0030), selected through its own endpoints.</summary>
    [RegularExpression("^(Flight|Hotel)$")]
    public string? Product { get; init; }
}

/// <summary>
/// Pays for the order and books it (authorize → book → capture). The token is the payment provider's opaque token from
/// its hosted card fields (never card data, SAQ-A); the amount always comes from the order, never from the client.
/// </summary>
public sealed class CheckoutRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string? PaymentMethodToken { get; init; }
}

/// <summary>
/// The customer's orders (ADR 0005, 0008). Thin: the customer id comes from the validated token only, never from the
/// request, and every order is looked up by its owner, so another customer's order is simply not found (no IDOR).
/// </summary>
internal static class OrderEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    // 201 when this request created the order; 200 when the same key and selection were already ordered (a replay). A
    // replay returns the order as it is NOW (the same resource, perhaps later in its lifecycle), not a stored response.
    public static async Task<Results<Created<OrderResponse>, Ok<OrderResponse>, ProblemHttpResult>> Create(
        CreateFlightOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        HttpContext http,
        CreateFlightOrderHandler handler,
        CancellationToken cancellationToken)
    {
        // The W3C trace id, so timeline entries line up with traces (observability.md).
        var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        var product = request.Product is "Hotel" ? OrderProduct.Hotel : OrderProduct.Flight;
        var command = new CreateFlightOrder(user.CustomerId()!, idempotencyKey ?? string.Empty, request.SelectedOfferId!.Value, correlationId, product);
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.IsSuccess)
        {
            var response = OrderResponse.From(result.Value.Order);
            return result.Value.Created ? TypedResults.Created($"/api/v1/orders/{response.OrderId}", response) : TypedResults.Ok(response);
        }

        return result.Error switch
        {
            CreateFlightOrderFailure.InvalidIdempotencyKey => Problem(StatusCodes.Status400BadRequest, "idempotency-key-required",
                $"Send a unique {IdempotencyKeyHeader} header (1 to {CreateFlightOrderHandler.MaxIdempotencyKeyLength} visible characters) and reuse it when retrying."),
            CreateFlightOrderFailure.IdempotencyKeyReused => Problem(StatusCodes.Status409Conflict, "idempotency-conflict",
                "This idempotency key was already used for another selection."),
            CreateFlightOrderFailure.SelectionAlreadyOrdered already => Problem(StatusCodes.Status409Conflict, "selection-already-ordered",
                "This selection already has an order.", new Dictionary<string, object?> { ["orderId"] = already.OrderId }),
            CreateFlightOrderFailure.SelectionUnavailable unavailable => Unavailable(unavailable.Reason, product),
            CreateFlightOrderFailure.CustomerRequired => Problem(StatusCodes.Status403Forbidden, "customer-required", "Sign in to create an order."),
            _ => throw new InvalidOperationException($"Unmapped order failure {result.Error.GetType().Name}."),
        };
    }

    // 200 with the outcome (booked, declined, a challenge to complete, or not booked and released); 202 while the booking
    // or payment outcome is still being settled (poll the order). The same key repeats the same attempt; a new key after
    // a decline starts another. Never a reason for a decline, never a manual review (generic declines).
    public static async Task<Results<Ok<CheckoutResponse>, Accepted<CheckoutResponse>, ProblemHttpResult>> Checkout(
        Guid orderId,
        CheckoutRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        HttpContext http,
        AuthorizeCheckoutHandler handler,
        IOrderStore store,
        CancellationToken cancellationToken)
    {
        // The same rule as order creation: stored as varchar, so only visible ASCII, never two keys that collapse into one.
        if (!CreateFlightOrderHandler.IsValidKey(idempotencyKey))
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency-key-required",
                $"Send a unique {IdempotencyKeyHeader} header (1 to {CreateFlightOrderHandler.MaxIdempotencyKeyLength} visible characters) and reuse it when retrying.");
        }

        var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        var result = await handler.HandleAsync(
            new AuthorizeCheckout(orderId, user.CustomerId()!, idempotencyKey!, request.PaymentMethodToken!, correlationId), cancellationToken);
        if (!result.IsSuccess)
        {
            return CheckoutProblem(result.Error);
        }

        var order = await store.FindOwnedAsync(orderId, user.CustomerId()!, cancellationToken);
        var response = CheckoutResponse.From(result.Value, order is null ? null : OrderResponse.From(order));
        return result.Value.Status is CheckoutStatus.BookingPending or CheckoutStatus.PaymentPending
            ? TypedResults.Accepted($"/api/v1/orders/{orderId}", response)
            : TypedResults.Ok(response);
    }

    public static async Task<Results<Ok<OrderResponse>, NotFound>> Get(
        Guid orderId, ClaimsPrincipal user, IOrderStore store, ICancellationRequestStore cancellations, CancellationToken cancellationToken) =>
        await store.FindOwnedAsync(orderId, user.CustomerId()!, cancellationToken) is { } order
            ? TypedResults.Ok(OrderResponse.From(order, await cancellations.FindLatestForOrderAsync(order.Id, cancellationToken)))
            : TypedResults.NotFound();

    private static ProblemHttpResult CheckoutProblem(CheckoutFailure failure) => failure switch
    {
        CheckoutFailure.NotFound => Problem(StatusCodes.Status404NotFound, "order-not-found", "This order was not found."),
        CheckoutFailure.InvalidRequest => Problem(StatusCodes.Status400BadRequest, "invalid-checkout", "This checkout request is not valid."),
        CheckoutFailure.NotAwaitingPayment => Problem(StatusCodes.Status409Conflict, "order-not-payable", "This order cannot be paid for now."),
        CheckoutFailure.IdempotencyKeyReused => Problem(StatusCodes.Status409Conflict, "idempotency-conflict", "This idempotency key was already used for another payment."),
        CheckoutFailure.PaymentInProgress => Problem(StatusCodes.Status409Conflict, "payment-in-progress", "Another payment for this order is still being processed."),
        CheckoutFailure.AuthorizedButNotBookable => Problem(StatusCodes.Status409Conflict, "order-not-bookable",
            "This booking cannot go ahead. You have not been charged, and the hold on your payment is released."),
        CheckoutFailure.PriceChanged => Problem(StatusCodes.Status422UnprocessableEntity, "price-changed",
            "The price changed. Confirm the new price before paying."),
        CheckoutFailure.OfferExpired => Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired", "This offer is no longer available. Please search again."),
        CheckoutFailure.SoldOut => Problem(StatusCodes.Status422UnprocessableEntity, "sold-out", "This flight is no longer available. Please search again."),
        CheckoutFailure.SupplierCannotBook => Problem(StatusCodes.Status422UnprocessableEntity, "not-bookable", "This flight cannot be booked online yet. Please search again."),
        CheckoutFailure.InvalidPaymentMethod => Problem(StatusCodes.Status422UnprocessableEntity, "invalid-payment-method", "This payment method cannot be used."),
        CheckoutFailure.TravellersIncomplete incomplete => Problem(StatusCodes.Status422UnprocessableEntity, "travellers-incomplete",
            "Give every traveller's details and the contact first.", new Dictionary<string, object?> { ["documentsRequired"] = incomplete.DocumentsRequired }),
        // Q10: a generic message, never that a limit exists.
        CheckoutFailure.AttemptLimitReached => Problem(StatusCodes.Status422UnprocessableEntity, "payment-not-accepted",
            "We could not take this payment. Please try again later or contact support."),
        _ => Problem(StatusCodes.Status503ServiceUnavailable, "try-again", "We could not complete this right now. You have not been charged. Please try again."),
    };

    // What the customer can do next, per failure-scenarios.md (F-01..F-03); supplier details are never passed on.
    private static ProblemHttpResult Unavailable(ItemUnavailable reason, OrderProduct product) => reason switch
    {
        ItemUnavailable.NeedsPriceCheck => Problem(StatusCodes.Status409Conflict, "price-check-required",
            product is OrderProduct.Hotel
                ? "Confirm the price with the hotel (and accept any change) before ordering."
                : "Confirm the price with the airline (and accept any change) before ordering."),
        ItemUnavailable.Expired or ItemUnavailable.NotFound => Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired",
            "This offer is no longer available. Please search again."),
        ItemUnavailable.SoldOut => Problem(StatusCodes.Status422UnprocessableEntity, "sold-out",
            product is OrderProduct.Hotel ? "This room is no longer available. Please search again." : "This flight is no longer available. Please search again."),
        ItemUnavailable.SupplierCannotBook => Problem(StatusCodes.Status422UnprocessableEntity, "not-bookable",
            product is OrderProduct.Hotel ? "This stay cannot be booked online yet. Please search again." : "This flight cannot be booked online yet. Please search again."),
        _ => Problem(StatusCodes.Status503ServiceUnavailable, "try-again",
            product is OrderProduct.Hotel ? "We could not check this stay right now. Please try again." : "We could not check this flight right now. Please try again."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title, IDictionary<string, object?>? extensions = null) =>
        TypedResults.Problem(statusCode: status, type: type, title: title, extensions: extensions);
}

/// <summary>The customer's order. Never the customer id, the idempotency key or supplier references.</summary>
/// <param name="CancellationRequest">The customer's latest cancellation request (ADR 0029), on the order's own page; null in lists.</param>
internal sealed record OrderResponse(
    Guid OrderId, string Status, DateTimeOffset CreatedAt, IReadOnlyList<OrderItemResponse> Items, CancellationRequestResponse? CancellationRequest)
{
    public static OrderResponse From(Order order, CancellationRequest? cancellation = null) => new(
        order.Id,
        order.Status.ToString(),
        order.CreatedAt,
        order.Items.Select(item => new OrderItemResponse(
            item.Id,
            item.Product.ToString(),
            item.SelectedOfferId,
            item.Status.ToString(),
            new OrderAmountResponse(item.AgreedPrice.Amount.ToString(CultureInfo.InvariantCulture), item.AgreedPrice.Currency.Value),
            item.OfferExpiresAt,
            item.AcceptedPriceQuoteId is not null,
            item.SupplierLocator,
            item.Ticketing?.ToString(),
            item.TravellerNeeds is { IsKnown: true } needs
                ? new TravellersNeededResponse(needs.Adults, needs.Children, needs.Infants, needs.DocumentsRequired)
                : null,
            item.CancellationTerms is { } terms
                ? new CancellationTermsResponse(
                    terms.Refundable,
                    terms.FreeUntil,
                    terms.PenaltyAmount is { } penalty
                        ? new OrderAmountResponse(penalty.ToString(CultureInfo.InvariantCulture), item.AgreedPrice.Currency.Value)
                        : null)
                : null)).ToList(),
        cancellation is null ? null : CancellationRequestResponse.From(cancellation));
}

/// <param name="PriceChangeAccepted">The customer accepted a changed price for this item before ordering (F-01).</param>
/// <param name="Travellers">The travellers to give before payment (Q9); null for an order made before this was recorded.</param>
/// <param name="Product">Flight or Hotel.</param>
/// <param name="BookingReference">The supplier's booking reference (a PNR, or a hotel confirmation number), once confirmed.</param>
/// <param name="Ticketing">Pending or Issued, once a flight is confirmed; null for a hotel stay.</param>
/// <param name="Cancellation">A hotel stay's agreed cancellation terms; null for a flight.</param>
internal sealed record OrderItemResponse(
    Guid ItemId, string Product, Guid SelectedOfferId, string Status, OrderAmountResponse AgreedPrice, DateTimeOffset OfferExpiresAt, bool PriceChangeAccepted,
    string? BookingReference, string? Ticketing, TravellersNeededResponse? Travellers, CancellationTermsResponse? Cancellation = null);

/// <summary>
/// A hotel rate's cancellation terms as agreed (ADR 0030 §7): the refund on cancellation follows them. Null for a flight.
/// After <paramref name="FreeCancellationUntil"/>, <paramref name="PenaltyAfterDeadline"/> is kept (the whole price when null).
/// </summary>
internal sealed record CancellationTermsResponse(bool Refundable, DateTimeOffset? FreeCancellationUntil, OrderAmountResponse? PenaltyAfterDeadline);

/// <param name="Outcome">Booked, BookingPending, BookingFailed, ActionRequired, Declined, PaymentPending, PaymentFailed or PaymentUnavailable.</param>
/// <param name="Payment">What the customer may be told about the payment: Accepted, Released, ActionRequired, Declined, Pending or Unavailable.</param>
/// <param name="CustomerAction">For a challenge only: what the provider's browser library needs to complete it. Never logged.</param>
internal sealed record CheckoutResponse(Guid OrderId, string Outcome, string Payment, string? CustomerAction, OrderResponse? Order)
{
    public static CheckoutResponse From(CheckoutResult result, OrderResponse? order) => new(
        result.OrderId,
        // A manual review is never named to the customer (generic declines): payment Unavailable, "contact support".
        result.Status is CheckoutStatus.PaymentManualReview ? "PaymentUnavailable" : result.Status.ToString(),
        result.CustomerState.ToString(),
        result.CustomerAction,
        order);

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"OrderId = {OrderId}, Outcome = {Outcome}, Payment = {Payment}, CustomerAction = {(CustomerAction is null ? "null" : "[redacted]")}");
        return true;
    }
}

/// <param name="DocumentsRequired">The supplier requires a travel document for each traveller (Q9: collected only then).</param>
internal sealed record TravellersNeededResponse(int Adults, int Children, int Infants, bool DocumentsRequired);

/// <summary>An amount as a decimal string with its ISO-4217 currency (api-design rules: never a JSON number).</summary>
internal sealed record OrderAmountResponse(string Amount, string Currency);
