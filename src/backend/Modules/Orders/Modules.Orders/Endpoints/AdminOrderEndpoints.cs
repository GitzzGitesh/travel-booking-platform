using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Endpoints;

/// <summary>Why a staff member checks a booking in review: a ticket reference or a short note, never personal data.</summary>
public sealed class BookingReviewCheckRequest
{
    [Required]
    [StringLength(CheckBookingReview.MaxReasonLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// A staff outcome for a booking in review (ADR 0025), with a ticket reference. <c>supplierReference</c> is the supplier
/// desk's cancellation reference (for <c>CancelledAtSupplier</c>). Accepting needs both confirmations, given by the person
/// who compared the supplier's booking with the order: the same travellers and flights, at no more than the agreed price.
/// </summary>
public sealed class BookingReviewOutcomeRequest
{
    [Required]
    public BookingReviewOutcome? Outcome { get; init; }

    [StringLength(CheckBookingReview.MaxReasonLength, MinimumLength = 3)]
    public string? SupplierReference { get; init; }

    public bool SameTravellersAndFlights { get; init; }

    public bool PriceNotAboveAgreed { get; init; }

    [Required]
    [StringLength(CheckBookingReview.MaxReasonLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// Orders for operations staff (ADR 0022). Thin: the staff id comes from the validated staff token only. Staff see our
/// ids, statuses, amounts, supplier references and the timeline, never travellers' personal data (that stays in Customers).
/// </summary>
internal static class AdminOrderEndpoints
{
    public const int MaxPageSize = 50;

    private static readonly FlightOrderItemStatus[] _queues =
        [FlightOrderItemStatus.ManualReview, FlightOrderItemStatus.PendingConfirmation, FlightOrderItemStatus.Booking];

    // Oldest first; the cursor is the last order's creation time (ticks) and id. itemStatus: ManualReview (default),
    // PendingConfirmation or Booking.
    public static async Task<Results<Ok<AdminOrderPage>, ProblemHttpResult>> Queue(
        IOrderStore store, CancellationToken cancellationToken, string? itemStatus = null, int limit = 20, string? cursor = null)
    {
        var status = FlightOrderItemStatus.ManualReview;
        if ((itemStatus is not null && (!Enum.TryParse(itemStatus, ignoreCase: false, out status) || !_queues.Contains(status)))
            || limit is < 1 or > MaxPageSize)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid-query", $"itemStatus is one of {string.Join(", ", _queues)}; limit is 1 to {MaxPageSize}.");
        }

        (DateTimeOffset, Guid)? after = null;
        if (cursor is not null)
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid-cursor", "The cursor is not valid.");
            }

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        var orders = await store.FindWithItemStatusAsync(status, after, limit, cancellationToken);
        var next = orders.Count == limit ? $"{orders[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{orders[^1].Id:N}" : null;
        return TypedResults.Ok(new AdminOrderPage([.. orders.Select(AdminOrderSummary.From)], next));
    }

    public static async Task<Results<Ok<AdminOrderDetail>, NotFound>> Get(Guid orderId, IOrderStore store, CancellationToken cancellationToken) =>
        await store.FindAsync(orderId, cancellationToken) is { } order ? TypedResults.Ok(AdminOrderDetail.From(order)) : TypedResults.NotFound();

    public static async Task<Results<Ok<BookingReviewCheckResponse>, ProblemHttpResult>> CheckReview(
        Guid orderId, Guid itemId, BookingReviewCheckRequest request, ClaimsPrincipal user, HttpContext http,
        ResolveBookingReviewHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CheckBookingReview(orderId, itemId, user.StaffId()!, request.Reason!, AuditSources.From(http)), cancellationToken);
        if (result.IsSuccess && result.Value.AlreadySettled)
        {
            // A repeat (or settled meanwhile): nothing was done; the current status tells the caller where it stands.
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: "not-in-review", title: "This booking is not in manual review.",
                extensions: new Dictionary<string, object?> { ["itemStatus"] = result.Value.Status.ToString() });
        }

        return result.IsSuccess
            ? TypedResults.Ok(new BookingReviewCheckResponse(result.Value.OrderId, result.Value.ItemId, result.Value.Status.ToString(), result.Value.Resolved))
            : result.Error switch
            {
                BookingReviewFailure.NotFound => Problem(StatusCodes.Status404NotFound, "order-item-not-found", "This order item was not found."),
                BookingReviewFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "The order changed at the same time. Check again."),
                _ => Problem(StatusCodes.Status400BadRequest, "invalid-request", "Give a ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
            };
    }

    public static async Task<Results<Ok<BookingReviewCheckResponse>, ProblemHttpResult>> RecordOutcome(
        Guid orderId, Guid itemId, BookingReviewOutcomeRequest request, ClaimsPrincipal user, HttpContext http,
        ResolveBookingReviewHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.RecordOutcomeAsync(
            new RecordBookingReviewOutcome(orderId, itemId, request.Outcome!.Value, request.SupplierReference, request.SameTravellersAndFlights,
                request.PriceNotAboveAgreed, user.StaffId()!, request.Reason!, AuditSources.From(http)),
            cancellationToken);
        if (result.IsSuccess && result.Value.AlreadySettled)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: "not-in-review", title: "This booking is not in manual review.",
                extensions: new Dictionary<string, object?> { ["itemStatus"] = result.Value.Status.ToString() });
        }

        return result.IsSuccess
            ? TypedResults.Ok(new BookingReviewCheckResponse(result.Value.OrderId, result.Value.ItemId, result.Value.Status.ToString(), result.Value.Resolved))
            : result.Error switch
            {
                BookingReviewFailure.NotFound => Problem(StatusCodes.Status404NotFound, "order-item-not-found", "This order item was not found."),
                BookingReviewFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "The order changed at the same time. Check again."),
                BookingReviewFailure.NoSupplierBookingSeen => Problem(StatusCodes.Status409Conflict, "no-supplier-booking-seen",
                    "No supplier booking was seen under our reference for this item: there is nothing to accept or cancel. Check it with the supplier instead."),
                _ => Problem(StatusCodes.Status400BadRequest, "invalid-request",
                    "Give a ticket reference; a cancellation needs the supplier's reference, and accepting needs both confirmations."),
            };
    }

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

internal sealed record AdminOrderPage(IReadOnlyList<AdminOrderSummary> Orders, string? NextCursor);

/// <param name="CustomerId">Our internal customer id (opaque); never the customer's personal data.</param>
internal sealed record AdminOrderSummary(Guid OrderId, string Status, DateTimeOffset CreatedAt, string CustomerId, string? PaymentId, IReadOnlyList<AdminOrderItem> Items)
{
    public static AdminOrderSummary From(Order order) => new(
        order.Id, order.Status.ToString(), order.CreatedAt, order.CustomerId, order.PaymentAuthorizationId, [.. order.Items.Select(AdminOrderItem.From)]);
}

internal sealed record AdminOrderItem(
    Guid ItemId, Guid SelectedOfferId, string Status, OrderAmountResponse AgreedPrice, string? ProviderId, string? BookingReference, string? Ticketing, DateTimeOffset? BookingStartedAt)
{
    public static AdminOrderItem From(FlightOrderItem item) => new(
        item.Id,
        item.SelectedOfferId,
        item.Status.ToString(),
        new OrderAmountResponse(item.AgreedPrice.Amount.ToString(CultureInfo.InvariantCulture), item.AgreedPrice.Currency.Value),
        item.ProviderId,
        item.SupplierLocator,
        item.Ticketing?.ToString(),
        item.BookingStartedAt);
}

internal sealed record AdminOrderDetail(AdminOrderSummary Order, IReadOnlyList<AdminTimelineEntry> Timeline)
{
    public static AdminOrderDetail From(Order order) => new(
        AdminOrderSummary.From(order),
        [.. order.Timeline.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new AdminTimelineEntry(e.At, e.Actor, e.ItemId, e.FromStatus, e.ToStatus, e.Reason, e.CorrelationId, e.ProviderReference))]);
}

internal sealed record AdminTimelineEntry(
    DateTimeOffset At, string Actor, Guid? ItemId, string? FromStatus, string ToStatus, string Reason, string? CorrelationId, string? ProviderReference);

/// <param name="Resolved">The check settled the booking (Confirmed or Failed); false when it stays in review.</param>
internal sealed record BookingReviewCheckResponse(Guid OrderId, Guid ItemId, string ItemStatus, bool Resolved);
