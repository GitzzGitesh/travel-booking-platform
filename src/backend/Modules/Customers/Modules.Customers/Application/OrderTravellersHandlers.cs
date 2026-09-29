using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Customers.Domain;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Customers.Application;

internal enum TravellersFailure
{
    /// <summary>No such order for this customer (another customer's order is not found either).</summary>
    OrderNotFound,

    /// <summary>The order no longer awaits payment, or its personal data is frozen (legal hold) or anonymised.</summary>
    NotEditable,

    /// <summary>The travellers do not match the order's passengers.</summary>
    WrongPassengers,

    /// <summary>A name, date of birth, gender or contact detail is not acceptable.</summary>
    InvalidDetails,

    /// <summary>The supplier does not require documents for this order: none is collected (Q9).</summary>
    DocumentsNotRequired,

    /// <summary>No traveller at that position; travellers are given first.</summary>
    TravellerNotFound,

    /// <summary>Documents cannot be stored now (no encryption key configured): nothing is kept in the clear.</summary>
    DocumentsUnavailable,

    /// <summary>Another request changed the travellers at the same time: try again.</summary>
    Conflict,
}

internal sealed record SaveOrderTravellers(
    Guid OrderId, string CustomerId, string Email, string Phone, IReadOnlyList<TravellerDetails> Travellers, string? CorrelationId = null)
{
    /// <summary>Ids only: the contact and travellers are personal data.</summary>
    public override string ToString() => $"SaveOrderTravellers {{ OrderId = {OrderId}, Travellers = {Travellers.Count} }}";
}

internal sealed record SaveTravelDocument(Guid OrderId, string CustomerId, int Position, TravelDocumentDetails Document, string? CorrelationId);

/// <summary>
/// Gives an order its booker contact and travellers (Q9): only for the customer's own order while it awaits payment,
/// for exactly its passengers. Retention dates come from the order's last travel date.
/// </summary>
internal sealed class SaveOrderTravellersHandler(
    IOrderTravellerNeeds orders, IPersonalDataStore store, TimeProvider timeProvider, IOptions<PersonalDataRetentionOptions> retention)
{
    public async Task<Result<OrderTravellerSet, TravellersFailure>> HandleAsync(SaveOrderTravellers command, CancellationToken cancellationToken)
    {
        if (await orders.FindAsync(command.OrderId, command.CustomerId, cancellationToken) is not { } needs)
        {
            return Failure(TravellersFailure.OrderNotFound);
        }

        if (!needs.Editable)
        {
            return Failure(TravellersFailure.NotEditable);
        }

        var now = timeProvider.GetUtcNow();
        var set = await store.FindSetAsync(command.OrderId, cancellationToken);
        if (set is null)
        {
            set = OrderTravellerSet.Start(command.OrderId, command.CustomerId, now);
            store.AddSet(set);
        }

        var replaced = set.Replace(command.Email, command.Phone, command.Travellers, new PassengerCounts(needs.Adults, needs.Children, needs.Infants),
            retention.Value.DatesFor(needs.LastTravelDate), now);
        if (!replaced.IsSuccess)
        {
            return Failure(replaced.Error switch
            {
                TravellerSetError.WrongPassengers => TravellersFailure.WrongPassengers,
                TravellerSetError.InvalidDetails => TravellersFailure.InvalidDetails,
                _ => TravellersFailure.NotEditable,
            });
        }

        // Documents of travellers whose details changed no longer match them: shredded at once.
        foreach (var document in await store.FindLiveDocumentsAsync(command.OrderId, cancellationToken))
        {
            if (replaced.Value.Contains(document.Id))
            {
                document.Shred(now);
                store.Audit(new DocumentAccess(document.Id, command.OrderId, DocumentAccessAction.Shredded, Actor(command.CustomerId), now, command.CorrelationId));
            }
        }

        return await store.TrySaveAsync(cancellationToken)
            ? Result<OrderTravellerSet, TravellersFailure>.Success(set)
            : Failure(TravellersFailure.Conflict);
    }

    internal static string Actor(string customerId) => $"customer:{customerId}";

    private static Result<OrderTravellerSet, TravellersFailure> Failure(TravellersFailure failure) =>
        Result<OrderTravellerSet, TravellersFailure>.Failure(failure);
}

/// <summary>
/// Stores a traveller's travel document (Sensitive PII, Q9): only when the supplier requires documents for the order,
/// encrypted with its own key before it reaches the database, and audited. A new document replaces (and shreds) the
/// previous one. The document is never logged or returned.
/// </summary>
internal sealed class SaveTravelDocumentHandler(IOrderTravellerNeeds orders, IPersonalDataStore store, IDocumentProtector protector, TimeProvider timeProvider)
{
    public async Task<Result<bool, TravellersFailure>> HandleAsync(SaveTravelDocument command, CancellationToken cancellationToken)
    {
        if (await orders.FindAsync(command.OrderId, command.CustomerId, cancellationToken) is not { } needs)
        {
            return Failure(TravellersFailure.OrderNotFound);
        }

        if (!needs.DocumentsRequired)
        {
            return Failure(TravellersFailure.DocumentsNotRequired);
        }

        if (!needs.Editable)
        {
            return Failure(TravellersFailure.NotEditable);
        }

        if (!command.Document.IsValid(needs.LastTravelDate))
        {
            return Failure(TravellersFailure.InvalidDetails);
        }

        if (await store.FindSetAsync(command.OrderId, cancellationToken) is not { } set
            || set.Travellers.SingleOrDefault(t => t.Position == command.Position) is not { } traveller)
        {
            return Failure(TravellersFailure.TravellerNotFound);
        }

        var documentId = Guid.NewGuid();
        var protectedDocument = protector.Protect(documentId, command.Document);
        if (!protectedDocument.IsSuccess)
        {
            return Failure(TravellersFailure.DocumentsUnavailable);
        }

        var now = timeProvider.GetUtcNow();
        if (set.AttachDocument(command.Position, documentId, now, out var previousDocument) is not null)
        {
            return Failure(TravellersFailure.NotEditable);
        }

        var actor = SaveOrderTravellersHandler.Actor(command.CustomerId);
        store.AddDocument(TravelDocument.Store(documentId, command.OrderId, traveller.Id, protectedDocument.Value, now));
        store.Audit(new DocumentAccess(documentId, command.OrderId, DocumentAccessAction.Stored, actor, now, command.CorrelationId));
        if (previousDocument is { } previous && await store.FindDocumentAsync(previous, cancellationToken) is { } old)
        {
            old.Shred(now);
            store.Audit(new DocumentAccess(previous, command.OrderId, DocumentAccessAction.Shredded, actor, now, command.CorrelationId));
        }

        return await store.TrySaveAsync(cancellationToken)
            ? Result<bool, TravellersFailure>.Success(true)
            : Failure(TravellersFailure.Conflict);
    }

    private static Result<bool, TravellersFailure> Failure(TravellersFailure failure) => Result<bool, TravellersFailure>.Failure(failure);
}

/// <summary>The customer's own travellers for an order; null for anyone else's order, an unknown one, or once anonymised.</summary>
internal sealed class OrderTravellersQuery(IPersonalDataStore store)
{
    public async Task<OrderTravellerSet?> FindAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
        await store.FindSetAsync(orderId, cancellationToken) is { AnonymisedAt: null } set && set.CustomerId == customerId ? set : null;
}

/// <summary><see cref="IOrderTravellers"/>: counts and flags only, for the order's owner.</summary>
internal sealed class OrderTravellersReadinessQuery(IPersonalDataStore store) : IOrderTravellers
{
    private static readonly OrderTravellersReadiness _nothing = new(0, 0, 0, false, 0);

    public async Task<OrderTravellersReadiness> GetReadinessAsync(Guid orderId, string customerId, CancellationToken cancellationToken)
    {
        if (await store.FindSetAsync(orderId, cancellationToken) is not { AnonymisedAt: null } set || set.CustomerId != customerId)
        {
            return _nothing;
        }

        var complete = set.Travellers.Where(t => t.Details is not null).ToList();
        return new OrderTravellersReadiness(
            complete.Count(t => t.Kind == PassengerKind.Adult),
            complete.Count(t => t.Kind == PassengerKind.Child),
            complete.Count(t => t.Kind == PassengerKind.Infant),
            set.ContactEmail is not null && set.ContactPhone is not null,
            complete.Count(t => t.DocumentId is not null));
    }
}

/// <summary>
/// Reads a traveller's document for a stated purpose (the supplier booking): decrypted in memory only, and every read
/// is recorded with its actor (security rules: access to Sensitive PII is audited).
/// </summary>
internal sealed class TravelDocumentReader(IPersonalDataStore store, IDocumentProtector protector, TimeProvider timeProvider)
{
    public async Task<Result<TravelDocumentDetails, TravellersFailure>> ReadAsync(Guid orderId, int position, string actor, string? correlationId, CancellationToken cancellationToken)
    {
        if (await store.FindSetAsync(orderId, cancellationToken) is not { } set
            || set.Travellers.SingleOrDefault(t => t.Position == position) is not { DocumentId: { } documentId }
            || await store.FindDocumentAsync(documentId, cancellationToken) is not { IsShredded: false, WrappedKey: { } key, Ciphertext: { } ciphertext } document)
        {
            return Result<TravelDocumentDetails, TravellersFailure>.Failure(TravellersFailure.TravellerNotFound);
        }

        var details = protector.Unprotect(documentId, new ProtectedDocument(document.KeyId, key, ciphertext));
        if (!details.IsSuccess)
        {
            return Result<TravelDocumentDetails, TravellersFailure>.Failure(TravellersFailure.DocumentsUnavailable);
        }

        // No read without its audit record: the plaintext is released only once the access is saved.
        store.Audit(new DocumentAccess(documentId, orderId, DocumentAccessAction.Read, actor, timeProvider.GetUtcNow(), correlationId));
        return await store.TrySaveAsync(cancellationToken)
            ? Result<TravelDocumentDetails, TravellersFailure>.Success(details.Value)
            : Result<TravelDocumentDetails, TravellersFailure>.Failure(TravellersFailure.Conflict);
    }
}
