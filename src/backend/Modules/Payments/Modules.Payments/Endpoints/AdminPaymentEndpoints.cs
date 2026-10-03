using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.Endpoints;

/// <summary>Why a staff member resolves a payment review: a ticket reference or a plain note, never personal or card data.</summary>
public sealed class PaymentReviewResolutionRequest
{
    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// Payment attempts for operations staff (ADR 0022). Thin: the staff id comes from the validated staff token only. Staff
/// see our ids, statuses, amounts, provider references and the history (including a decline reason, which customers
/// never see), never card data (there is none) or customers' personal data.
/// </summary>
internal static class AdminPaymentEndpoints
{
    public static async Task<Results<Ok<AdminPaymentAttempt>, ProblemHttpResult>> Get(
        Guid attemptId, IPaymentAttemptStore store, Microsoft.Extensions.Options.IOptions<PaymentHoldOptions> holds, CancellationToken cancellationToken) =>
        await store.FindAsync(attemptId, cancellationToken) is { } attempt
            ? TypedResults.Ok(AdminPaymentAttempt.From(attempt, holds.Value, await store.FindRefundsAsync(attempt.Id, cancellationToken)))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, type: "payment-not-found", title: "This payment attempt was not found.");

    // ADR 0027: a refund in manual review is settled only by a lookup by our key, never by someone's statement.
    public static async Task<Results<Ok<RefundReviewResolutionResponse>, ProblemHttpResult>> ResolveRefund(
        Guid refundId, PaymentReviewResolutionRequest request, ClaimsPrincipal user, HttpContext http, ResolveRefundReviewHandler handler,
        CancellationToken cancellationToken)
    {
        var (outcome, status) = await handler.HandleAsync(refundId, user.StaffId()!, request.Reason!, AuditSources.From(http), cancellationToken);
        return outcome switch
        {
            RefundReviewOutcome.Resolved or RefundReviewOutcome.StillNeedsReview =>
                TypedResults.Ok(new RefundReviewResolutionResponse(refundId, status?.ToString(), outcome is RefundReviewOutcome.Resolved)),
            RefundReviewOutcome.NotInReview => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: "not-in-review",
                title: "This refund is not in manual review.", extensions: new Dictionary<string, object?> { ["refundStatus"] = status?.ToString() }),
            RefundReviewOutcome.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, type: "refund-not-found", title: "This refund was not found."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "invalid-request",
                title: "Give a ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
        };
    }

    // The attempt is looked up with the provider by our reference and moves only to what the provider holds.
    public static async Task<Results<Ok<PaymentReviewResolutionResponse>, ProblemHttpResult>> Resolve(
        Guid attemptId, PaymentReviewResolutionRequest request, ClaimsPrincipal user, HttpContext http,
        ResolvePaymentReviewHandler handler, CancellationToken cancellationToken)
    {
        var source = AuditSources.From(http);
        var result = await handler.HandleAsync(
            new ResolvePaymentReview(attemptId, user.StaffId()!, request.Reason!, source.CorrelationId, source), cancellationToken);
        return result.Outcome switch
        {
            PaymentReviewOutcome.Resolved or PaymentReviewOutcome.StillNeedsReview =>
                TypedResults.Ok(new PaymentReviewResolutionResponse(attemptId, result.Status?.ToString(), result.Outcome is PaymentReviewOutcome.Resolved)),
            PaymentReviewOutcome.AlreadyResolved => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: "not-in-review",
                title: "This payment is not in manual review.", extensions: new Dictionary<string, object?> { ["paymentStatus"] = result.Status?.ToString() }),
            PaymentReviewOutcome.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, type: "payment-not-found", title: "This payment attempt was not found."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, type: "invalid-request",
                title: "Give a ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
        };
    }

    // Q10: customers whose payment attempts were refused by the limits repeatedly in the last 24 hours (no automatic block).
    public static async Task<Ok<IReadOnlyList<AttemptLimitReviewEntry>>> AttemptLimitReview(PaymentAttemptReviewList reviewList, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<AttemptLimitReviewEntry>>([.. (await reviewList.FindAsync(cancellationToken)).Select(c => new AttemptLimitReviewEntry(c.CustomerId, c.Trips))]);
}

/// <param name="Resolved">The review was settled by the provider's answer; false when it stays in review (the check is recorded).</param>
internal sealed record PaymentReviewResolutionResponse(Guid AttemptId, string? Status, bool Resolved);

/// <param name="Resolved">The refund's review was settled by the provider's answer; false when it stays in review.</param>
internal sealed record RefundReviewResolutionResponse(Guid RefundId, string? Status, bool Resolved);

/// <summary>One refund of the payment (ADR 0027), as stored: our id (the refund case), amount, status and the provider's refund id.</summary>
internal sealed record AdminRefund(Guid RefundId, AdminAmount Amount, string Status, string? Reason, string? ProviderRefundId, DateTimeOffset RequestedAt, DateTimeOffset? SettledAt)
{
    public static AdminRefund From(RefundRecord r) =>
        new(r.Id, AdminAmount.From(r.Amount), r.Status.ToString(), r.Reason, r.ProviderRefundId, r.RequestedAt, r.SettledAt);
}

/// <param name="CustomerId">Our internal customer id (opaque).</param>
internal sealed record AttemptLimitReviewEntry(string CustomerId, int Refusals);

internal sealed record AdminPaymentAttempt(
    Guid AttemptId, Guid OrderId, string CustomerId, string Status, AdminAmount Amount, string? ProviderId, string? ProviderPaymentId, string? DeclineReason,
    DateTimeOffset CreatedAt, DateTimeOffset? ReleaseRequestedAt, DateTimeOffset? CaptureRequestedAt, AdminAmount? CaptureAmount, IReadOnlyList<AdminPaymentEvent> History,
    DateTimeOffset? AuthorizedAt, DateTimeOffset? HoldExpiresAt, AdminAmount? Refunded, IReadOnlyList<AdminRefund> Refunds)
{
    public static AdminPaymentAttempt From(PaymentAttempt attempt, PaymentHoldOptions holds, IReadOnlyList<RefundRecord> refunds) => new(
        attempt.Id,
        attempt.OrderId,
        attempt.CustomerId,
        attempt.Status.ToString(),
        AdminAmount.From(attempt.Amount),
        attempt.ProviderId,
        attempt.ProviderPaymentId,
        attempt.DeclineReason,
        attempt.CreatedAt,
        attempt.ReleaseRequestedAt,
        attempt.CaptureRequestedAt,
        attempt.CaptureAmount is { } capture ? AdminAmount.From(capture) : null,
        [.. attempt.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new AdminPaymentEvent(e.At, e.Actor, e.FromStatus, e.ToStatus, e.Reason, e.CorrelationId, e.ProviderReference))],
        attempt.AuthorizedAt,
        attempt.MayHoldFunds ? holds.ExpiresAt(attempt) : null, // only while funds may be held: the deadline to settle by
        attempt.CaptureAmount is { } captured ? AdminAmount.From(new BuildingBlocks.Money(attempt.RefundedAmountValue, captured.Currency)) : null,
        [.. refunds.Select(AdminRefund.From)]);
}

internal sealed record AdminAmount(string Amount, string Currency)
{
    public static AdminAmount From(BuildingBlocks.Money money) => new(money.Amount.ToString(CultureInfo.InvariantCulture), money.Currency.Value);
}

internal sealed record AdminPaymentEvent(DateTimeOffset At, string Actor, string? FromStatus, string ToStatus, string Reason, string? CorrelationId, string? ProviderReference);
