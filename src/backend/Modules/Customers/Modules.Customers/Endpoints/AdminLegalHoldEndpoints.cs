using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Customers.Application;

namespace TravelBooking.Modules.Customers.Endpoints;

/// <summary>Place (<see cref="Hold"/> true) or release a legal hold, on instruction from legal: a case or ticket reference as the reason.</summary>
public sealed class LegalHoldStateRequest
{
    [Required]
    public bool? Hold { get; init; }

    [Required]
    [StringLength(AuditReasons.MaxLength, MinimumLength = 3)]
    public string? Reason { get; init; }
}

/// <summary>
/// The legal hold on an order's personal data (Q9, ADR 0020, ADR 0022): while held, nothing is anonymised or shredded and
/// the travellers cannot change. Idempotent by state (the same request again changes nothing). Never returns personal data.
/// </summary>
internal static class AdminLegalHoldEndpoints
{
    public static async Task<Results<Ok<LegalHoldStateResponse>, ProblemHttpResult>> Set(
        Guid orderId, LegalHoldStateRequest request, ClaimsPrincipal user, HttpContext http, LegalHoldHandler handler, CancellationToken cancellationToken)
    {
        var source = AuditSources.From(http);
        var outcome = await handler.HandleAsync(
            new LegalHoldRequest(orderId, request.Hold!.Value, user.StaffId()!, request.Reason!, source.CorrelationId, source), cancellationToken);
        return outcome switch
        {
            LegalHoldOutcome.Applied or LegalHoldOutcome.Unchanged =>
                TypedResults.Ok(new LegalHoldStateResponse(orderId, request.Hold.Value, outcome is LegalHoldOutcome.Applied)),
            LegalHoldOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "personal-data-not-found", "No personal data is kept for this order."),
            LegalHoldOutcome.Anonymised => Problem(StatusCodes.Status409Conflict, "personal-data-anonymised", "This order's personal data is already anonymised: nothing is left to hold."),
            LegalHoldOutcome.ReleaseNeedsApproval => Problem(StatusCodes.Status409Conflict, "release-requires-approval",
                "Releasing a legal hold needs a second person's approval: request the release instead (ADR 0026)."),
            LegalHoldOutcome.Conflict => Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "The data changed at the same time. Try again."),
            _ => Problem(StatusCodes.Status400BadRequest, "invalid-request", "Give a case or ticket reference (3 to 200 letters, digits, spaces or . _ : / # -)."),
        };
    }

    private static ProblemHttpResult Problem(int status, string type, string title) => TypedResults.Problem(statusCode: status, type: type, title: title);
}

/// <param name="Changed">False when the hold was already in the requested state (nothing done).</param>
internal sealed record LegalHoldStateResponse(Guid OrderId, bool Held, bool Changed);
