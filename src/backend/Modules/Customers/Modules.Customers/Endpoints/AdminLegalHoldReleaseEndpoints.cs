using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Endpoints;

/// <summary>Why the hold should be released, or why a request is withdrawn: a case or ticket reference.</summary>
public sealed class LegalHoldReleaseRequestRequest
{
    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>Approve (true) or reject another staff member's release request, with a case or ticket reference.</summary>
public sealed class LegalHoldReleaseDecisionRequest
{
    [Required]
    public bool? Approve { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// Releasing a legal hold (ADR 0026): requested by one staff member, decided by a different one with the approve
/// permission. Thin: the acting staff member (staff id and workforce account) comes from the validated identity only.
/// </summary>
internal static class AdminLegalHoldReleaseEndpoints
{
    public const int MaxPending = 100;

    public static async Task<Results<Ok<LegalHoldStatusResponse>, ProblemHttpResult>> Status(
        Guid orderId, IPersonalDataStore store, ILegalHoldReleaseStore releases, CancellationToken cancellationToken)
    {
        if (await store.FindSetAsync(orderId, cancellationToken) is not { } set)
        {
            return Problem(StatusCodes.Status404NotFound, "personal-data-not-found", "No personal data is kept for this order.");
        }

        var pending = await releases.FindPendingForOrderAsync(orderId, cancellationToken);
        return TypedResults.Ok(new LegalHoldStatusResponse(orderId, set.LegalHold, set.AnonymisedAt is not null, set.PurgeNotBefore,
            pending is null ? null : LegalHoldReleaseResponse.From(pending)));
    }

    public static async Task<Results<Created<LegalHoldReleaseResponse>, ProblemHttpResult>> Request(
        Guid orderId, LegalHoldReleaseRequestRequest request, ClaimsPrincipal user, HttpContext http, LegalHoldReleaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor)
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        var (outcome, created) = await handler.RequestAsync(orderId, request.Reason!, actor, AuditSources.From(http), cancellationToken);
        return outcome is LegalHoldReleaseOutcome.Done
            ? TypedResults.Created($"/api/admin/v1/legal-hold/release-requests/{created!.Id}", LegalHoldReleaseResponse.From(created))
            : Failure(outcome);
    }

    public static async Task<Ok<IReadOnlyList<LegalHoldReleaseResponse>>> Pending(ILegalHoldReleaseStore releases, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<LegalHoldReleaseResponse>>([.. (await releases.FindPendingAsync(MaxPending, cancellationToken)).Select(LegalHoldReleaseResponse.From)]);

    public static async Task<Results<Ok<LegalHoldReleaseResponse>, ProblemHttpResult>> Decide(
        Guid requestId, LegalHoldReleaseDecisionRequest request, ClaimsPrincipal user, HttpContext http, LegalHoldReleaseHandler handler,
        CancellationToken cancellationToken) =>
        await DecideAsync(requestId, request.Approve!.Value, request.Reason!, user, isChecker: true, http, handler, cancellationToken);

    // The requester withdraws their own request (a rejection by its maker); nobody else may withdraw it.
    public static async Task<Results<Ok<LegalHoldReleaseResponse>, ProblemHttpResult>> Withdraw(
        Guid requestId, LegalHoldReleaseRequestRequest request, ClaimsPrincipal user, HttpContext http, LegalHoldReleaseHandler handler,
        CancellationToken cancellationToken) =>
        await DecideAsync(requestId, approve: false, request.Reason!, user, isChecker: false, http, handler, cancellationToken);

    private static async Task<Results<Ok<LegalHoldReleaseResponse>, ProblemHttpResult>> DecideAsync(
        Guid requestId, bool approve, string reason, ClaimsPrincipal user, bool isChecker, HttpContext http, LegalHoldReleaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor || (isChecker && !user.HasPermission(StaffPermissions.PersonalDataLegalHoldApprove)))
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        var (outcome, decided) = await handler.DecideAsync(requestId, approve, reason, actor, isChecker, AuditSources.From(http), cancellationToken);
        return outcome is LegalHoldReleaseOutcome.Done ? TypedResults.Ok(LegalHoldReleaseResponse.From(decided!)) : Failure(outcome);
    }

    private static LegalHoldActor? Actor(ClaimsPrincipal user) =>
        user.StaffId() is { } staffId && user.AccountObjectId() is { Length: > 0 } account ? new LegalHoldActor(staffId, account) : null;

    private static ProblemHttpResult Failure(LegalHoldReleaseOutcome outcome) => outcome switch
    {
        LegalHoldReleaseOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "not-found", "No such order data or release request."),
        LegalHoldReleaseOutcome.NotHeld => Problem(StatusCodes.Status409Conflict, "not-held", "This order's personal data is not under a legal hold (or is already anonymised)."),
        LegalHoldReleaseOutcome.AlreadyPending => Problem(StatusCodes.Status409Conflict, "release-pending", "A release for this order is already waiting for a decision."),
        LegalHoldReleaseOutcome.SelfApproval => Problem(StatusCodes.Status403Forbidden, "self-approval-not-allowed", "Nobody approves their own release request."),
        LegalHoldReleaseOutcome.NotRequester => Problem(StatusCodes.Status403Forbidden, "withdrawal-requester-only", "Only the person who requested the release can withdraw it."),
        LegalHoldReleaseOutcome.AlreadyDecided => Problem(StatusCodes.Status409Conflict, "already-decided", "This request was already decided."),
        LegalHoldReleaseOutcome.Expired => Problem(StatusCodes.Status409Conflict, "request-expired", "This request waited too long to be approved: reject it and request again."),
        LegalHoldReleaseOutcome.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Try again."),
        _ => Problem(StatusCodes.Status400BadRequest, "invalid-request", "Give a case or ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

internal sealed record LegalHoldReleaseResponse(
    Guid RequestId, Guid OrderId, string Status, string RequestedBy, DateTimeOffset RequestedAt, string Reason, string? DecidedBy, DateTimeOffset? DecidedAt)
{
    public static LegalHoldReleaseResponse From(LegalHoldReleaseRequest r) =>
        new(r.Id, r.OrderId, r.Status.ToString(), $"staff:{r.RequestedBy}", r.RequestedAt, r.Reason, r.DecidedBy is null ? null : $"staff:{r.DecidedBy}", r.DecidedAt);
}

/// <param name="PurgeNotBefore">After an approved release, the purge waits until this date (the grace period).</param>
internal sealed record LegalHoldStatusResponse(Guid OrderId, bool Held, bool Anonymised, DateOnly? PurgeNotBefore, LegalHoldReleaseResponse? PendingRelease);
