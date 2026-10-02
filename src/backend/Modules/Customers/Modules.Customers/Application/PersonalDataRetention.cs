using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Domain;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Customers.Application;

internal enum LegalHoldOutcome
{
    Applied,

    /// <summary>Already in the requested state: nothing changed (idempotent).</summary>
    Unchanged,

    /// <summary>No personal data is kept for this order.</summary>
    NotFound,

    /// <summary>No actor or reason given.</summary>
    Invalid,

    /// <summary>The data is already anonymised: nothing left to hold.</summary>
    Anonymised,

    /// <summary>A held order's release needs a second person's approval (ADR 0026): use a release request.</summary>
    ReleaseNeedsApproval,

    Conflict,
}

/// <param name="Actor">An opaque staff id (never a name or email); from the staff principal once staff identity exists.</param>
/// <param name="Reason">A case or ticket reference and a short note, with no personal data.</param>
internal sealed record LegalHoldRequest(Guid OrderId, bool Hold, string Actor, string Reason, string? CorrelationId = null, AuditSource? Source = null);

/// <summary>
/// Places or releases a legal hold on an order's personal data (Q9): while held, nothing is anonymised or shredded and
/// the data is not changed. A staff action (permission personal-data.legal-hold, ADR 0022): recorded in the append-only
/// retention events and the audit log, in the same save.
/// </summary>
internal sealed class LegalHoldHandler(IPersonalDataStore store, TimeProvider timeProvider)
{
    public const int MaxActorLength = 64;
    public const int MaxReasonLength = AuditReasons.MaxLength;
    public const string Action = "personal-data.legal-hold";

    public async Task<LegalHoldOutcome> HandleAsync(LegalHoldRequest request, CancellationToken cancellationToken)
    {
        if (request.Actor is not { Length: > 0 and <= MaxActorLength } || !request.Actor.All(c => c is > ' ' and <= '~')
            || !AuditReasons.IsValid(request.Reason))
        {
            return LegalHoldOutcome.Invalid;
        }

        if (await store.FindSetAsync(request.OrderId, cancellationToken) is not { } set)
        {
            return LegalHoldOutcome.NotFound;
        }

        if (set.AnonymisedAt is not null)
        {
            return LegalHoldOutcome.Anonymised;
        }

        if (set.LegalHold == request.Hold)
        {
            return LegalHoldOutcome.Unchanged;
        }

        // Placing a hold is one step; releasing one is maker-checker (ADR 0026: LegalHoldReleaseHandler).
        if (!request.Hold)
        {
            return LegalHoldOutcome.ReleaseNeedsApproval;
        }

        var now = timeProvider.GetUtcNow();
        set.PlaceLegalHold(now);

        var actor = $"staff:{request.Actor}";
        store.Audit(new RetentionEvent(request.OrderId, request.Hold ? RetentionAction.LegalHoldPlaced : RetentionAction.LegalHoldReleased,
            actor, request.Reason, now, request.CorrelationId));
        store.Audit(AuditEntry.For(request.Source ?? new AuditSource(request.CorrelationId, null, null), now, actor, Action, $"order:{request.OrderId}",
            request.Hold ? "not held" : "held", $"{(request.Hold ? "held" : "not held")}; {request.Reason}"));
        return await store.TrySaveAsync(cancellationToken) ? LegalHoldOutcome.Applied : LegalHoldOutcome.Conflict;
    }
}

/// <summary>
/// Applies the retention of one order's personal data (Q9): documents past their date are crypto-shredded, and the
/// travellers and contact past theirs are anonymised. Never under a legal hold. Idempotent.
/// </summary>
internal sealed class PersonalDataPurger(IPersonalDataStore store, TimeProvider timeProvider)
{
    public const string Actor = "system:personal-data-retention";

    public async Task<bool> PurgeAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (await store.FindSetAsync(orderId, cancellationToken) is not { } set || !set.MayPurge(today))
        {
            return false;
        }

        var changed = false;
        if (set.DocumentsRetainUntil < today || (set.AnonymisedAt is null && set.RetainUntil < today))
        {
            var documents = await store.FindLiveDocumentsAsync(orderId, cancellationToken);
            foreach (var document in documents)
            {
                document.Shred(now);
                store.Audit(new DocumentAccess(document.Id, orderId, DocumentAccessAction.Shredded, Actor, now, null));
            }

            if (documents.Count > 0)
            {
                set.DocumentsShredded(now);
                store.Audit(new RetentionEvent(orderId, RetentionAction.DocumentsShredded, Actor, "Documents past their retention date", now, null));
                changed = true;
            }
        }

        if (set.AnonymisedAt is null && set.RetainUntil < today)
        {
            set.Anonymise(now);
            store.Audit(new RetentionEvent(orderId, RetentionAction.Anonymised, Actor, "Personal data past its retention date", now, null));
            changed = true;
        }

        return changed && await store.TrySaveAsync(cancellationToken);
    }
}

/// <summary>
/// Orders abandoned an order (ADR 0007 inbox: once per event). Its retention is shortened (Q9, approved 2026-09-29), and
/// the purge runs for it at once, so its documents are shredded now unless a legal hold applies. The hourly purge
/// anonymises the rest after the grace period. The event only comes once no payment for the order is unsettled.
/// </summary>
internal sealed class OrderAbandonedHandler(
    IPersonalDataStore store, PersonalDataPurger purger, TimeProvider timeProvider, IOptions<PersonalDataRetentionOptions> retention)
    : IIntegrationEventHandler<OrderAbandoned>
{
    public const string Name = "customers.order-abandoned";
    public const string Actor = "system:orders";

    public async Task HandleAsync(OrderAbandoned integrationEvent, CancellationToken cancellationToken)
    {
        if (await store.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (await store.FindSetAsync(integrationEvent.OrderId, cancellationToken) is { AnonymisedAt: null } set
            && set.OrderAbandoned(DateOnly.FromDateTime(integrationEvent.OccurredAt.UtcDateTime), retention.Value.PersonalDataDaysAfterAbandonment, now))
        {
            store.Audit(new RetentionEvent(integrationEvent.OrderId, RetentionAction.ShortenedForAbandonedOrder, Actor,
                "The order was abandoned before booking", now, integrationEvent.CorrelationId));
        }

        store.MarkConsumed(integrationEvent.EventId, Name, now);
        if (!await store.TrySaveAsync(cancellationToken))
        {
            // The set changed at the same moment (e.g. a legal hold): fail so the outbox delivers the event again.
            throw new InvalidOperationException($"The travellers of order {integrationEvent.OrderId} changed concurrently.");
        }

        await purger.PurgeAsync(integrationEvent.OrderId, cancellationToken); // if this fails, the hourly purge does it
    }
}

/// <summary>The Worker's hourly sweep: every order whose personal data is due, each in its own scope.</summary>
internal sealed partial class PurgePersonalDataJob(
    IPersonalDataStore store, IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<PurgePersonalDataJob> logger) : IBackgroundJob
{
    public const string Name = "customers.purge-personal-data";
    public const int BatchSize = 100;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        foreach (var orderId in await store.FindDueForPurgeAsync(DateOnly.FromDateTime(startedAt.UtcDateTime), BatchSize, cancellationToken))
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<PersonalDataPurger>().PurgeAsync(orderId, cancellationToken))
                {
                    handled++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, orderId, exception.GetType().Name);
            }
        }

        return handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purging personal data of order {OrderId} failed ({Error}); it stays on the work list")]
    private static partial void LogFailed(ILogger logger, Guid orderId, string error);
}
