using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Application;

internal interface ILegalHoldReleaseStore
{
    void Add(LegalHoldReleaseRequest request);

    Task<LegalHoldReleaseRequest?> FindAsync(Guid requestId, CancellationToken cancellationToken);

    Task<LegalHoldReleaseRequest?> FindPendingForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Pending requests, oldest first (the approvers' list).</summary>
    Task<IReadOnlyList<LegalHoldReleaseRequest>> FindPendingAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>The acting staff member: our staff id and their workforce account, from the validated staff identity only.</summary>
internal sealed record LegalHoldActor(string StaffId, string Account);

internal enum LegalHoldReleaseOutcome
{
    Done,
    Invalid,
    NotFound,

    /// <summary>The order's data is not under a hold (nothing to release), or already anonymised.</summary>
    NotHeld,

    /// <summary>A release for this order is already waiting for a decision.</summary>
    AlreadyPending,

    SelfApproval,
    NotRequester,
    AlreadyDecided,
    Expired,
    Conflict,
}

/// <summary>
/// Releasing a legal hold (ADR 0026): one staff member requests it (the hold stays in force), a different one with the
/// approve permission decides. On approval the hold is released and the purge waits the grace period
/// (<c>Customers:Retention:LegalHoldReleaseGraceDays</c>). Every request and decision is audited in the same save, and
/// maker-checker refusals are security events.
/// </summary>
internal sealed partial class LegalHoldReleaseHandler(
    IPersonalDataStore store, ILegalHoldReleaseStore releases, IOptions<PersonalDataRetentionOptions> retention, TimeProvider timeProvider,
    ILogger<LegalHoldReleaseHandler> logger)
{
    public const string RequestAction = "personal-data.legal-hold.release-request";
    public const string ApproveAction = "personal-data.legal-hold.release-approve";
    public const string RejectAction = "personal-data.legal-hold.release-reject";

    public async Task<(LegalHoldReleaseOutcome Outcome, LegalHoldReleaseRequest? Request)> RequestAsync(
        Guid orderId, string reason, LegalHoldActor actor, AuditSource source, CancellationToken cancellationToken)
    {
        if (!AuditReasons.IsValid(reason) || !IsValid(actor))
        {
            return (LegalHoldReleaseOutcome.Invalid, null);
        }

        if (await store.FindSetAsync(orderId, cancellationToken) is not { } set)
        {
            return (LegalHoldReleaseOutcome.NotFound, null);
        }

        if (!set.LegalHold || set.AnonymisedAt is not null)
        {
            return (LegalHoldReleaseOutcome.NotHeld, null);
        }

        if (await releases.FindPendingForOrderAsync(orderId, cancellationToken) is { } pending)
        {
            return (LegalHoldReleaseOutcome.AlreadyPending, pending);
        }

        var now = timeProvider.GetUtcNow();
        var request = LegalHoldReleaseRequest.For(orderId, actor.StaffId, actor.Account, reason, now);
        releases.Add(request);
        store.Audit(AuditEntry.For(source, now, $"staff:{actor.StaffId}", RequestAction, $"order:{orderId}", "held", $"release requested ({request.Id}); {reason}"));
        return await store.TrySaveAsync(cancellationToken)
            ? (LegalHoldReleaseOutcome.Done, request)
            : (LegalHoldReleaseOutcome.AlreadyPending, null); // one pending request per order (unique index), or a concurrent change
    }

    /// <summary>A checker approves or rejects; the requester may only withdraw (reject) their own request.</summary>
    public async Task<(LegalHoldReleaseOutcome Outcome, LegalHoldReleaseRequest? Request)> DecideAsync(
        Guid requestId, bool approve, string reason, LegalHoldActor actor, bool isChecker, AuditSource source, CancellationToken cancellationToken)
    {
        if (!AuditReasons.IsValid(reason) || !IsValid(actor))
        {
            return (LegalHoldReleaseOutcome.Invalid, null);
        }

        if (await releases.FindAsync(requestId, cancellationToken) is not { } request)
        {
            return (LegalHoldReleaseOutcome.NotFound, null);
        }

        var requester = request.IsRequester(actor.StaffId, actor.Account);
        if (!isChecker && !(requester && !approve))
        {
            Refused(actor, request, "not-the-requester");
            return (LegalHoldReleaseOutcome.NotRequester, request);
        }

        var now = timeProvider.GetUtcNow();
        var set = approve ? await store.FindSetAsync(request.OrderId, cancellationToken) : null;
        var refusal = approve ? request.Approve(actor.StaffId, actor.Account, reason, now) : request.Reject(actor.StaffId, reason, now);
        switch (refusal)
        {
            case LegalHoldReleaseRefusal.SelfApproval:
                Refused(actor, request, "self-approval");
                return (LegalHoldReleaseOutcome.SelfApproval, request);
            case LegalHoldReleaseRefusal.AlreadyDecided:
                return (LegalHoldReleaseOutcome.AlreadyDecided, request);
            case LegalHoldReleaseRefusal.Expired:
                return (LegalHoldReleaseOutcome.Expired, request);
        }

        var staff = $"staff:{actor.StaffId}";
        if (approve)
        {
            if (set is not { LegalHold: true, AnonymisedAt: null })
            {
                return (LegalHoldReleaseOutcome.NotHeld, request); // the hold was released or the data anonymised meanwhile
            }

            set.ReleaseLegalHold(now, retention.Value.LegalHoldReleaseGraceDays);
            store.Audit(new RetentionEvent(request.OrderId, RetentionAction.LegalHoldReleased, staff,
                $"Release approved (requested by staff:{request.RequestedBy}); purge not before {set.PurgeNotBefore:yyyy-MM-dd}; {reason}", now, source.CorrelationId));
        }

        store.Audit(AuditEntry.For(source, now, staff, approve ? ApproveAction : RejectAction, $"order:{request.OrderId}",
            "held", $"{(approve ? "released" : "held")} (request {request.Id}); {reason}"));
        return await store.TrySaveAsync(cancellationToken) ? (LegalHoldReleaseOutcome.Done, request) : (LegalHoldReleaseOutcome.Conflict, null);
    }

    private static bool IsValid(LegalHoldActor actor) =>
        actor.StaffId is { Length: > 0 and <= LegalHoldHandler.MaxActorLength } && actor.Account is { Length: > 0 and <= 128 };

    // Security event (ADR 0022: maker-checker refusals): ids only, never the account or the reason.
    private void Refused(LegalHoldActor actor, LegalHoldReleaseRequest request, string why) =>
        LogRefused(logger, actor.StaffId, request.Id, why);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "LegalHoldReleaseRefused",
        Message = "Security: staff member {StaffId} was refused on legal-hold release request {RequestId} ({Why})")]
    private static partial void LogRefused(ILogger logger, string staffId, Guid requestId, string why);
}
