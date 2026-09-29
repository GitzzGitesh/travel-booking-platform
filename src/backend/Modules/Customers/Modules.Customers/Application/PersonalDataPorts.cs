using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Application;

/// <summary>The personal-data store (Customers schema only): traveller sets, encrypted documents, and their audit trails.</summary>
internal interface IPersonalDataStore
{
    /// <summary>The order's traveller set, tracked for changes; null if none was given yet.</summary>
    Task<OrderTravellerSet?> FindSetAsync(Guid orderId, CancellationToken cancellationToken);

    void AddSet(OrderTravellerSet set);

    Task<TravelDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The order's documents not shredded yet, tracked.</summary>
    Task<IReadOnlyList<TravelDocument>> FindLiveDocumentsAsync(Guid orderId, CancellationToken cancellationToken);

    void AddDocument(TravelDocument document);

    void Audit(DocumentAccess access);

    void Audit(RetentionEvent retentionEvent);

    /// <summary>Saves; false if another request changed the set first (optimistic concurrency) or it was added twice.</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Orders whose personal data or documents are past their retention date and not on legal hold: the purge's work
    /// list, oldest first.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindDueForPurgeAsync(DateOnly today, int limit, CancellationToken cancellationToken);
}

internal enum DocumentProtectionError
{
    /// <summary>No key-encryption key is configured: documents cannot be stored or read (fails closed).</summary>
    NotConfigured,

    /// <summary>The ciphertext does not decrypt with its key (tampered, or the key is gone).</summary>
    Unreadable,
}

/// <summary>
/// Envelope encryption for travel documents (ADR 0020): a fresh data key per document, wrapped by a key-encryption key
/// that never leaves its store. The document id is bound into the ciphertext, so a ciphertext copied onto another
/// document does not decrypt.
/// </summary>
internal interface IDocumentProtector
{
    Result<ProtectedDocument, DocumentProtectionError> Protect(Guid documentId, TravelDocumentDetails details);

    Result<TravelDocumentDetails, DocumentProtectionError> Unprotect(Guid documentId, ProtectedDocument document);
}

/// <summary>
/// <c>Customers:Retention</c> (Q9, approved 2026-09-28): personal data is anonymised a number of months after the last
/// travel date, and documents are shredded a number of days after it. Configurable; the approved values are the defaults.
/// Financial records are kept by their own modules for the financial period (provisionally 10 years, pending Q14 and
/// counsel); they hold internal ids only.
/// </summary>
internal sealed class PersonalDataRetentionOptions
{
    public const string SectionName = "Customers:Retention";

    public int PersonalDataMonthsAfterTravel { get; set; } = 25;

    public int DocumentDaysAfterTravel { get; set; } = 30;

    public RetentionDates DatesFor(DateOnly lastTravelDate) =>
        new(lastTravelDate, lastTravelDate.AddMonths(PersonalDataMonthsAfterTravel), lastTravelDate.AddDays(DocumentDaysAfterTravel));
}
