using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Application;

internal enum LegalHoldOutcome
{
    Applied,

    /// <summary>Already in the requested state: nothing changed (idempotent).</summary>
    Unchanged,

    /// <summary>No personal data is kept for this order.</summary>
    NotFound,

    /// <summary>No actor or reason given, or the data is already anonymised (nothing left to hold).</summary>
    Invalid,

    Conflict,
}

/// <param name="Actor">An opaque staff id (never a name or email); from the staff principal once staff identity exists.</param>
/// <param name="Reason">A case or ticket reference and a short note, with no personal data.</param>
internal sealed record LegalHoldRequest(Guid OrderId, bool Hold, string Actor, string Reason, string? CorrelationId = null);

/// <summary>
/// Places or releases a legal hold on an order's personal data (Q9): while held, nothing is anonymised or shredded and
/// the data is not changed. An operations action: its admin endpoint comes with staff identity. Recorded in the
/// append-only retention events.
/// </summary>
internal sealed class LegalHoldHandler(IPersonalDataStore store, TimeProvider timeProvider)
{
    public const int MaxActorLength = 64;
    public const int MaxReasonLength = 200;

    public async Task<LegalHoldOutcome> HandleAsync(LegalHoldRequest request, CancellationToken cancellationToken)
    {
        if (request.Actor is not { Length: > 0 and <= MaxActorLength } || !request.Actor.All(c => c is > ' ' and <= '~')
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > MaxReasonLength || request.Reason.Any(char.IsControl))
        {
            return LegalHoldOutcome.Invalid;
        }

        if (await store.FindSetAsync(request.OrderId, cancellationToken) is not { } set)
        {
            return LegalHoldOutcome.NotFound;
        }

        if (set.AnonymisedAt is not null)
        {
            return LegalHoldOutcome.Invalid;
        }

        if (set.LegalHold == request.Hold)
        {
            return LegalHoldOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        if (request.Hold)
        {
            set.PlaceLegalHold(now);
        }
        else
        {
            set.ReleaseLegalHold(now);
        }

        store.Audit(new RetentionEvent(request.OrderId, request.Hold ? RetentionAction.LegalHoldPlaced : RetentionAction.LegalHoldReleased,
            $"operator:{request.Actor}", request.Reason, now, request.CorrelationId));
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
        if (await store.FindSetAsync(orderId, cancellationToken) is not { LegalHold: false } set)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
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
