using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Application;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Access.Domain;

namespace TravelBooking.Modules.Access.Endpoints;

/// <summary>Request a role grant or revocation for a workforce account (its Entra object id), with a ticket reference.</summary>
public sealed class RoleChangeRequestRequest
{
    [Required]
    [StringLength(StaffMember.MaxObjectIdLength, MinimumLength = 1)]
    public string? ObjectId { get; init; }

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string? Role { get; init; }

    [Required]
    public RoleChangeAction? Action { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>Approve (true) or reject a pending request, with a ticket reference.</summary>
public sealed class RoleChangeDecisionRequest
{
    [Required]
    public bool? Approve { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// Managed staff role grants (ADR 0022: maker-checker). Thin: the acting staff member comes from the validated staff
/// token only (our staff id and their own workforce account), never from the request.
/// </summary>
internal static class AdminAccessEndpoints
{
    public const int MaxPageSize = 100;

    // Newest first; the cursor is the last request's time (ticks) and id.
    public static async Task<Results<Ok<RoleChangePage>, ProblemHttpResult>> ListRequests(
        IRoleGrantStore store, CancellationToken cancellationToken, string? status = null, int limit = 50, string? cursor = null)
    {
        var wanted = RoleChangeStatus.Pending;
        if ((status is not null && (!Enum.TryParse(status, ignoreCase: false, out wanted) || !Enum.IsDefined(wanted))) || limit is < 1 or > MaxPageSize)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid-query", $"status is Pending, Approved or Rejected; limit is 1 to {MaxPageSize}.");
        }

        (DateTimeOffset, Guid)? before = null;
        if (cursor is not null)
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid-cursor", "The cursor is not valid.");
            }

            before = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        var page = await store.FindRequestsAsync(wanted, before, limit, cancellationToken);
        var next = page.Count == limit ? $"{page[^1].RequestedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{page[^1].Id:N}" : null;
        return TypedResults.Ok(new RoleChangePage([.. page.Select(RoleChangeResponse.From)], next));
    }

    // Effective access: managed grants and the configuration bootstrap (which is changed only in configuration).
    public static async Task<Ok<IReadOnlyList<RoleGrantResponse>>> ListGrants(
        IRoleGrantStore store, Microsoft.Extensions.Options.IOptionsMonitor<AccessOptions> configuration, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<RoleGrantResponse>>(
        [
            .. (await store.FindActiveGrantsAsync(cancellationToken)).Select(g => new RoleGrantResponse(g.ObjectId, g.Role, "managed", g.GrantedAt, g.GrantedByRequestId)),
            .. configuration.CurrentValue.RoleAssignments.SelectMany(a => a.Roles.Select(role => new RoleGrantResponse(a.ObjectId, role, "configuration", null, null))),
        ]);

    public static async Task<Results<Created<RoleChangeResponse>, ProblemHttpResult>> Request(
        RoleChangeRequestRequest request, ClaimsPrincipal user, HttpContext http, RoleChangeHandler handler, CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor)
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        RoleChangeKind? kind = request.Action switch
        {
            RoleChangeAction.Grant => RoleChangeKind.Grant,
            RoleChangeAction.Revoke => RoleChangeKind.Revoke,
            _ => null,
        };
        if (kind is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid-request", "action is Grant or Revoke.");
        }

        var result = await handler.RequestAsync(request.ObjectId!, request.Role!, kind.Value, request.Reason!, actor, AuditSources.From(http), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/admin/v1/access/role-changes/{result.Value.Id}", RoleChangeResponse.From(result.Value))
            : Failure(result.Error);
    }

    public static async Task<Results<Ok<RoleChangeResponse>, ProblemHttpResult>> Decide(
        Guid requestId, RoleChangeDecisionRequest request, ClaimsPrincipal user, HttpContext http, RoleChangeHandler handler, CancellationToken cancellationToken)
    {
        if (Actor(user) is not { } actor)
        {
            return Problem(StatusCodes.Status403Forbidden, "staff-account-required", "A staff account is required.");
        }

        var result = await handler.DecideAsync(requestId, request.Approve!.Value, request.Reason!, actor, AuditSources.From(http), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(RoleChangeResponse.From(result.Value)) : Failure(result.Error);
    }

    // Our staff id (from the identity only the Access module creates) and the workforce account of the validated staff
    // token or session.
    private static StaffActor? Actor(ClaimsPrincipal user) =>
        user.StaffId() is { } staffId && user.AccountObjectId() is { Length: > 0 } objectId ? new StaffActor(staffId, objectId) : null;

    private static ProblemHttpResult Failure(RoleChangeFailure failure) => failure switch
    {
        RoleChangeFailure.NotFound => Problem(StatusCodes.Status404NotFound, "role-change-not-found", "This role change request was not found."),
        RoleChangeFailure.NothingToChange => Problem(StatusCodes.Status409Conflict, "nothing-to-change", "The account already has this role (or does not have it to revoke)."),
        RoleChangeFailure.PendingExists => Problem(StatusCodes.Status409Conflict, "role-change-pending", "A request for this account and role is already waiting for a decision."),
        RoleChangeFailure.SelfApproval => Problem(StatusCodes.Status403Forbidden, "self-approval-not-allowed",
            "Nobody requests a change to their own access, or approves their own request."),
        RoleChangeFailure.WithdrawalOnly => Problem(StatusCodes.Status403Forbidden, "revocation-withdrawal-only",
            "A revocation can only be withdrawn by the person who requested it."),
        RoleChangeFailure.GrantedByConfiguration => Problem(StatusCodes.Status409Conflict, "granted-by-configuration",
            "This role is granted by the environment's configuration (the bootstrap): change it there."),
        RoleChangeFailure.RequesterNoLongerAuthorized => Problem(StatusCodes.Status409Conflict, "requester-no-longer-authorized",
            "The person who requested this change may no longer request changes: reject it and request again if needed."),
        RoleChangeFailure.Expired => Problem(StatusCodes.Status409Conflict, "request-expired", "This request waited too long to be approved: request again."),
        RoleChangeFailure.AlreadyDecided => Problem(StatusCodes.Status409Conflict, "already-decided", "This request was already decided."),
        RoleChangeFailure.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "It changed at the same time. Try again."),
        _ => Problem(StatusCodes.Status400BadRequest, "invalid-request",
            "Give the account's object id (a lowercase GUID), a known role and a ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
    };

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

internal sealed record RoleChangeResponse(
    Guid RequestId, string ObjectId, string Role, string Action, string Status, string RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt)
{
    public static RoleChangeResponse From(RoleChangeRequest r) =>
        new(r.Id, r.ObjectId, r.Role, r.Kind.ToString(), r.Status.ToString(), $"staff:{r.RequestedBy}", r.RequestedAt, r.DecidedBy is null ? null : $"staff:{r.DecidedBy}", r.DecidedAt);
}

internal sealed record RoleChangePage(IReadOnlyList<RoleChangeResponse> Requests, string? NextCursor);

/// <param name="Source">"managed" (granted by an approved request) or "configuration" (the bootstrap).</param>
internal sealed record RoleGrantResponse(string ObjectId, string Role, string Source, DateTimeOffset? GrantedAt, Guid? GrantedByRequestId);
