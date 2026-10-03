using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Endpoints;

/// <summary>
/// Open a refund case (ADR 0027). A cancellation names the confirmed items cancelled at the supplier, the supplier desk's
/// reference and what the supplier refunds for them (<c>supplierRefund</c>, a decimal string in the order's currency); its
/// refund is computed by the server. A goodwill refund gives its <c>amount</c> (a decimal string). Never a card or a
/// client-computed total.
/// </summary>
public sealed class RefundCaseRequest
{
    [Required]
    public RefundCaseKind? Kind { get; init; }

    [MaxLength(9)]
    public Guid[]? ItemIds { get; init; }

    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? SupplierReference { get; init; }

    [RegularExpression(@"^\d{1,13}(\.\d{1,4})?$")]
    public string? SupplierRefund { get; init; }

    [RegularExpression(@"^\d{1,13}(\.\d{1,4})?$")]
    public string? Amount { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>Approve (true) or reject another staff member's refund case, with a ticket reference.</summary>
public sealed class RefundDecisionRequest
{
    [Required]
    public bool? Approve { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>Why the requester withdraws their refund case: a ticket reference.</summary>
public sealed class RefundWithdrawalRequest
{
    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// Cancellations and refunds for staff (ADR 0027). Thin: the acting staff member (staff id and workforce account) comes
/// from the validated identity only, and every amount from the server.
/// </summary>
internal static class AdminRefundEndpoints
{
    // 201 when this request opened the case; 200 when the same key and request were already sent (a replay).
    public static async Task<Results<Created<RefundCaseResponse>, Ok<RefundCaseResponse>, ProblemHttpResult>> Open(
        Guid orderId, RefundCaseRequest request, [Microsoft.AspNetCore.Mvc.FromHeader(Name = OrderEndpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user, HttpContext http, RefundCaseHandler handler, CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor)
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        var (outcome, opened) = await handler.OpenAsync(
            new OpenRefundCase(orderId, request.Kind!.Value, request.ItemIds ?? [], request.SupplierReference, Parse(request.SupplierRefund), Parse(request.Amount),
                request.Reason!, idempotencyKey ?? string.Empty, actor, AuditSources.From(http)),
            cancellationToken);
        return outcome switch
        {
            RefundCaseOutcome.Done => TypedResults.Created($"/api/admin/v1/refund-cases/{opened!.Id}", RefundCaseResponse.From(opened)),
            RefundCaseOutcome.Replayed => TypedResults.Ok(RefundCaseResponse.From(opened!)),
            _ => Failure(outcome),
        };
    }

    public static async Task<Ok<IReadOnlyList<RefundCaseResponse>>> ForOrder(Guid orderId, IRefundCaseStore cases, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<RefundCaseResponse>>([.. (await cases.FindForOrderAsync(orderId, cancellationToken)).Select(RefundCaseResponse.From)]);

    public static async Task<Ok<IReadOnlyList<RefundCaseResponse>>> Pending(IRefundCaseStore cases, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<RefundCaseResponse>>([.. (await cases.FindPendingAsync(RefundCaseHandler.MaxPending, cancellationToken)).Select(RefundCaseResponse.From)]);

    public static Task<Results<Ok<RefundCaseResponse>, ProblemHttpResult>> Decide(
        Guid caseId, RefundDecisionRequest request, ClaimsPrincipal user, HttpContext http, RefundCaseHandler handler, CancellationToken cancellationToken) =>
        DecideAsync(caseId, request.Approve!.Value, request.Reason!, user, isChecker: true, http, handler, cancellationToken);

    public static Task<Results<Ok<RefundCaseResponse>, ProblemHttpResult>> Withdraw(
        Guid caseId, RefundWithdrawalRequest request, ClaimsPrincipal user, HttpContext http, RefundCaseHandler handler, CancellationToken cancellationToken) =>
        DecideAsync(caseId, approve: false, request.Reason!, user, isChecker: false, http, handler, cancellationToken);

    private static async Task<Results<Ok<RefundCaseResponse>, ProblemHttpResult>> DecideAsync(
        Guid caseId, bool approve, string reason, ClaimsPrincipal user, bool isChecker, HttpContext http, RefundCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor || (isChecker && !user.HasPermission(StaffPermissions.RefundsApprove)))
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        var (outcome, decided) = await handler.DecideAsync(caseId, approve, reason, actor, isChecker, AuditSources.From(http), cancellationToken);
        return outcome is RefundCaseOutcome.Done ? TypedResults.Ok(RefundCaseResponse.From(decided!)) : Failure(outcome);
    }

    private static decimal? Parse(string? value) =>
        value is null ? null : decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static RefundActor? Actor(ClaimsPrincipal user) =>
        user.StaffId() is { } staffId && user.AccountObjectId() is { Length: > 0 } account ? new RefundActor(staffId, account) : null;

    private static ProblemHttpResult Failure(RefundCaseOutcome outcome) => outcome switch
    {
        RefundCaseOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "not-found", "No such order or refund case."),
        RefundCaseOutcome.IdempotencyConflict => Problem(StatusCodes.Status409Conflict, "idempotency-conflict", "This idempotency key was already used for another refund request."),
        RefundCaseOutcome.NotCaptured => Problem(StatusCodes.Status409Conflict, "not-captured", "Nothing was charged for this order: there is nothing to refund."),
        RefundCaseOutcome.ItemNotCancellable => Problem(StatusCodes.Status409Conflict, "item-not-cancellable", "Only confirmed items of this order can be cancelled."),
        RefundCaseOutcome.ExceedsRefundable => Problem(StatusCodes.Status409Conflict, "exceeds-refundable", "The amount exceeds what can still be refunded."),
        RefundCaseOutcome.SelfApproval => Problem(StatusCodes.Status403Forbidden, "self-approval-not-allowed", "Nobody approves their own refund."),
        RefundCaseOutcome.NotRequester => Problem(StatusCodes.Status403Forbidden, "withdrawal-requester-only", "Only the person who opened the case can withdraw it."),
        RefundCaseOutcome.AlreadyDecided => Problem(StatusCodes.Status409Conflict, "already-decided", "This case was already decided."),
        RefundCaseOutcome.Expired => Problem(StatusCodes.Status409Conflict, "request-expired", "This case waited too long to be approved: reject it and open a new one."),
        RefundCaseOutcome.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Try again."),
        _ => Problem(StatusCodes.Status400BadRequest, "invalid-request",
            "A cancellation needs confirmed items, the supplier's reference and its refund; a goodwill refund needs an amount; both need a ticket reference and an Idempotency-Key header."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <param name="Amount">What the customer gets back, as computed by the server.</param>
internal sealed record RefundCaseResponse(
    Guid CaseId, Guid OrderId, string Kind, string Status, OrderAmountResponse Amount, string? SupplierRefund, string Fee, string? SupplierReference,
    IReadOnlyList<Guid> ItemIds, string Reason, string RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt, DateTimeOffset? SettledAt)
{
    public static RefundCaseResponse From(RefundCase c) => new(
        c.Id, c.OrderId, c.Kind.ToString(), c.Status.ToString(), new OrderAmountResponse(c.AmountValue.ToString(CultureInfo.InvariantCulture), c.CurrencyCode),
        c.SupplierRefundValue?.ToString(CultureInfo.InvariantCulture), c.FeeValue.ToString(CultureInfo.InvariantCulture), c.SupplierReference,
        c.CancelledItemIds, c.Reason, $"staff:{c.RequestedBy}", c.RequestedAt, c.DecidedBy is null ? null : $"staff:{c.DecidedBy}", c.DecidedAt, c.SettledAt);
}
