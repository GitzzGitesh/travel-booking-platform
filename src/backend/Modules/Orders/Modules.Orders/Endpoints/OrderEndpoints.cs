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
/// Creates the signed-in customer's order for a flight selection that was revalidated (and, if its price changed,
/// accepted). Only the selection id is sent: the price comes from Flights, never from the client. Public because the
/// .NET 10 validation source generator skips internal types (ADR 0003).
/// </summary>
public sealed class CreateFlightOrderRequest
{
    [Required]
    public Guid? SelectedOfferId { get; init; }
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
        var command = new CreateFlightOrder(user.CustomerId()!, idempotencyKey ?? string.Empty, request.SelectedOfferId!.Value, correlationId);
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
                "This idempotency key was already used for another flight selection."),
            CreateFlightOrderFailure.SelectionAlreadyOrdered already => Problem(StatusCodes.Status409Conflict, "selection-already-ordered",
                "This flight selection already has an order.", already.OrderId is { } own ? new Dictionary<string, object?> { ["orderId"] = own } : null),
            CreateFlightOrderFailure.SelectionUnavailable unavailable => Unavailable(unavailable.Reason),
            CreateFlightOrderFailure.CustomerRequired => Problem(StatusCodes.Status403Forbidden, "customer-required", "Sign in to create an order."),
            _ => throw new InvalidOperationException($"Unmapped order failure {result.Error.GetType().Name}."),
        };
    }

    public static async Task<Results<Ok<OrderResponse>, NotFound>> Get(Guid orderId, ClaimsPrincipal user, IOrderStore store, CancellationToken cancellationToken) =>
        await store.FindOwnedAsync(orderId, user.CustomerId()!, cancellationToken) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : TypedResults.NotFound();

    // What the customer can do next, per failure-scenarios.md (F-01..F-03); supplier details are never passed on.
    private static ProblemHttpResult Unavailable(FlightSelectionUnavailable reason) => reason switch
    {
        FlightSelectionUnavailable.NeedsPriceCheck => Problem(StatusCodes.Status409Conflict, "price-check-required",
            "Confirm the price with the airline (and accept any change) before ordering."),
        FlightSelectionUnavailable.Expired or FlightSelectionUnavailable.NotFound => Problem(StatusCodes.Status422UnprocessableEntity, "offer-expired",
            "This offer is no longer available. Please search again."),
        FlightSelectionUnavailable.SoldOut => Problem(StatusCodes.Status422UnprocessableEntity, "sold-out",
            "This flight is no longer available. Please search again."),
        FlightSelectionUnavailable.SupplierCannotBook => Problem(StatusCodes.Status422UnprocessableEntity, "not-bookable",
            "This flight cannot be booked online yet. Please search again."),
        _ => Problem(StatusCodes.Status503ServiceUnavailable, "try-again", "We could not check this flight right now. Please try again."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title, IDictionary<string, object?>? extensions = null) =>
        TypedResults.Problem(statusCode: status, type: type, title: title, extensions: extensions);
}

/// <summary>The customer's order. Never the customer id, the idempotency key or supplier references.</summary>
internal sealed record OrderResponse(Guid OrderId, string Status, DateTimeOffset CreatedAt, IReadOnlyList<OrderItemResponse> Items)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.Status.ToString(),
        order.CreatedAt,
        order.Items.Select(item => new OrderItemResponse(
            item.Id,
            item.SelectedOfferId,
            item.Status.ToString(),
            new OrderAmountResponse(item.AgreedPrice.Amount.ToString(CultureInfo.InvariantCulture), item.AgreedPrice.Currency.Value),
            item.OfferExpiresAt,
            item.AcceptedPriceQuoteId is not null)).ToList());
}

/// <param name="PriceChangeAccepted">The customer accepted a changed price for this item before ordering (F-01).</param>
internal sealed record OrderItemResponse(
    Guid ItemId, Guid SelectedOfferId, string Status, OrderAmountResponse AgreedPrice, DateTimeOffset OfferExpiresAt, bool PriceChangeAccepted);

/// <summary>An amount as a decimal string with its ISO-4217 currency (api-design rules: never a JSON number).</summary>
internal sealed record OrderAmountResponse(string Amount, string Currency);
