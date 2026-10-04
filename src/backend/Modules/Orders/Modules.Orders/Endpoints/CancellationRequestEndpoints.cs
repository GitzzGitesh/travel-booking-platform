using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Endpoints;

/// <summary>Decline a customer's cancellation request (it cannot be cancelled), with a ticket reference. Never shown to the customer.</summary>
public sealed class CancellationRequestDeclineRequest
{
    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// "My trips" and cancellation requests (ADR 0029). The customer comes from the validated token or session only; another
/// customer's order or request is not found. Thin: one handler call each.
/// </summary>
internal static class CancellationRequestEndpoints
{
    public const int MaxPageSize = 50;
    private const string _idempotencyKeyHeader = "Idempotency-Key";

    // Newest first; the cursor is the last order's creation time (ticks) and id.
    public static async Task<Results<Ok<OrderPage>, ProblemHttpResult>> ListMine(
        ClaimsPrincipal user, IOrderHistory history, CancellationToken cancellationToken, int limit = 20, string? cursor = null)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid-query", $"limit is 1 to {MaxPageSize}.");
        }

        (DateTimeOffset, Guid)? before = null;
        if (cursor is not null)
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid-cursor", "The cursor is not valid.");
            }

            before = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        var page = await history.FindForCustomerAsync(user.CustomerId()!, before, limit, cancellationToken);
        var next = page.Count == limit ? $"{page[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{page[^1].Id:N}" : null;
        return TypedResults.Ok(new OrderPage([.. page.Select(o => OrderResponse.From(o))], next));
    }

    public static async Task<Results<Created<CancellationRequestResponse>, Ok<CancellationRequestResponse>, ProblemHttpResult>> Request(
        Guid orderId, [FromHeader(Name = _idempotencyKeyHeader)] string? idempotencyKey, ClaimsPrincipal user, HttpContext http,
        CancellationRequestHandler handler, CancellationToken cancellationToken)
    {
        var (outcome, request) = await handler.RequestAsync(orderId, user.CustomerId()!, idempotencyKey, AuditSources.From(http).CorrelationId, cancellationToken);
        return outcome switch
        {
            CancellationRequestOutcome.Done => TypedResults.Created($"/api/v1/orders/{orderId}", CancellationRequestResponse.From(request!)),
            CancellationRequestOutcome.Replayed => TypedResults.Ok(CancellationRequestResponse.From(request!)),
            CancellationRequestOutcome.AlreadyRequested => Problem(StatusCodes.Status409Conflict, "cancellation-already-requested",
                "You have already asked to cancel this booking.", new Dictionary<string, object?> { ["requestId"] = request!.Id }),
            CancellationRequestOutcome.IdempotencyConflict => Problem(StatusCodes.Status409Conflict, "idempotency-conflict",
                "This idempotency key was already used for another request."),
            CancellationRequestOutcome.NotCancellable => Problem(StatusCodes.Status409Conflict, "not-cancellable",
                "This order has no confirmed booking to cancel."),
            CancellationRequestOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "order-not-found", "This order was not found."),
            CancellationRequestOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, "idempotency-key-required",
                $"Send a unique {_idempotencyKeyHeader} header (1 to {CancellationRequest.MaxKeyLength} visible characters) and reuse it when retrying."),
            _ => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Please try again."),
        };
    }

    public static async Task<Results<Ok<CancellationRequestResponse>, ProblemHttpResult>> Withdraw(
        Guid orderId, Guid requestId, ClaimsPrincipal user, HttpContext http, CancellationRequestHandler handler, CancellationToken cancellationToken)
    {
        var (outcome, request) = await handler.WithdrawAsync(orderId, requestId, user.CustomerId()!, AuditSources.From(http).CorrelationId, cancellationToken);
        return outcome switch
        {
            CancellationRequestOutcome.Done or CancellationRequestOutcome.Replayed => TypedResults.Ok(CancellationRequestResponse.From(request!)),
            CancellationRequestOutcome.NotOpen => Problem(StatusCodes.Status409Conflict, "not-open",
                "This request was already handled.", new Dictionary<string, object?> { ["requestStatus"] = request!.Status.ToString() }),
            CancellationRequestOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "cancellation-request-not-found", "This cancellation request was not found."),
            _ => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Please try again."),
        };
    }

    // Staff (ADR 0029): the open requests, oldest first, and declining one; completing one is opening the cancellation case.
    public static async Task<Ok<IReadOnlyList<AdminCancellationRequest>>> Open(ICancellationRequestStore requests, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<AdminCancellationRequest>>(
            [.. (await requests.FindOpenAsync(CancellationRequestHandler.MaxOpen, cancellationToken)).Select(AdminCancellationRequest.From)]);

    public static async Task<Results<Ok<AdminCancellationRequest>, ProblemHttpResult>> Decline(
        Guid requestId, CancellationRequestDeclineRequest request, ClaimsPrincipal user, HttpContext http, CancellationRequestHandler handler,
        CancellationToken cancellationToken)
    {
        var (outcome, declined) = await handler.DeclineAsync(requestId, user.StaffId()!, request.Reason!, AuditSources.From(http), cancellationToken);
        return outcome switch
        {
            CancellationRequestOutcome.Done => TypedResults.Ok(AdminCancellationRequest.From(declined!)),
            CancellationRequestOutcome.NotOpen => Problem(StatusCodes.Status409Conflict, "not-open",
                "This request was already handled.", new Dictionary<string, object?> { ["requestStatus"] = declined!.Status.ToString() }),
            CancellationRequestOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "cancellation-request-not-found", "This cancellation request was not found."),
            CancellationRequestOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, "invalid-request",
                "Give a ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
            _ => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Try again."),
        };
    }

    private static ProblemHttpResult Problem(int status, string type, string title, IDictionary<string, object?>? extensions = null) =>
        TypedResults.Problem(statusCode: status, type: type, title: title, extensions: extensions);
}

/// <param name="NextCursor">Pass as <c>cursor</c> for the next (older) page; null on the last page.</param>
internal sealed record OrderPage(IReadOnlyList<OrderResponse> Orders, string? NextCursor);

/// <summary>A customer's cancellation request, as the customer sees it: never the staff note.</summary>
internal sealed record CancellationRequestResponse(Guid RequestId, string Status, DateTimeOffset RequestedAt, DateTimeOffset? ResolvedAt)
{
    public static CancellationRequestResponse From(CancellationRequest request) =>
        new(request.Id, request.Status.ToString(), request.RequestedAt, request.ResolvedAt);
}

/// <summary>A cancellation request for operations: the customer id is internal (pseudonymous), never personal data.</summary>
internal sealed record AdminCancellationRequest(
    Guid RequestId, Guid OrderId, string CustomerId, string Status, DateTimeOffset RequestedAt, DateTimeOffset? ResolvedAt, string? ResolvedBy,
    string? ResolutionNote)
{
    public static AdminCancellationRequest From(CancellationRequest request) =>
        new(request.Id, request.OrderId, request.CustomerId, request.Status.ToString(), request.RequestedAt, request.ResolvedAt, request.ResolvedBy,
            request.ResolutionNote);
}
