namespace TravelBooking.Modules.Customers.Domain;

internal enum TravelDocumentType
{
    Passport,
    IdentityCard,
}

/// <summary>
/// A traveller's travel document (Sensitive PII, Q9), collected only when the supplier requires it. Only ciphertext is
/// stored: the document is encrypted with its own data key, itself wrapped by a key-encryption key (ADR 0020). Shredding
/// deletes the wrapped key and the ciphertext from the live database. Copies made before (database backups, exports) stay
/// readable while their key-encryption key exists, so that key is rotated and retired on a schedule (ADR 0020).
/// </summary>
internal sealed class TravelDocument
{
    private TravelDocument()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid TravellerId { get; private set; }

    /// <summary>The key-encryption key that wrapped this document's data key (for rotation).</summary>
    public string KeyId { get; private set; } = string.Empty;

    public byte[]? WrappedKey { get; private set; }

    public byte[]? Ciphertext { get; private set; }

    public DateTimeOffset StoredAt { get; private set; }

    public DateTimeOffset? ShreddedAt { get; private set; }

    public bool IsShredded => ShreddedAt is not null;

    public static TravelDocument Store(Guid id, Guid orderId, Guid travellerId, ProtectedDocument protectedDocument, DateTimeOffset at) => new()
    {
        Id = id,
        OrderId = orderId,
        TravellerId = travellerId,
        KeyId = protectedDocument.KeyId,
        WrappedKey = protectedDocument.WrappedKey,
        Ciphertext = protectedDocument.Ciphertext,
        StoredAt = at,
    };

    /// <summary>Crypto-shreds the document: its data key and ciphertext are deleted. Idempotent.</summary>
    public void Shred(DateTimeOffset at)
    {
        if (IsShredded)
        {
            return;
        }

        WrappedKey = null;
        Ciphertext = null;
        ShreddedAt = at;
    }
}

/// <summary>A document as stored: ciphertext, and its data key wrapped by the named key-encryption key.</summary>
internal sealed record ProtectedDocument(string KeyId, byte[] WrappedKey, byte[] Ciphertext);

/// <summary>A travel document's fields, only ever held in memory while it is stored or read for the supplier.</summary>
internal sealed record TravelDocumentDetails(TravelDocumentType Type, string Number, string IssuingCountry, string Nationality, DateOnly ExpiryDate)
{
    public const int MaxNumberLength = 20;

    /// <summary>Never prints the document number (security rules).</summary>
    public override string ToString() => $"{Type} document (number withheld)";

    /// <summary>
    /// The number in letters and digits; ISO 3166 alpha-2 countries; valid through the last travel date. The supplier
    /// and airline make the final checks.
    /// </summary>
    public bool IsValid(DateOnly lastTravelDate) =>
        Enum.IsDefined(Type)
        && Number is { Length: > 0 and <= MaxNumberLength } && Number.All(char.IsAsciiLetterOrDigit)
        && IsCountry(IssuingCountry) && IsCountry(Nationality)
        && ExpiryDate >= lastTravelDate;

    private static bool IsCountry(string code) => code is { Length: 2 } && code.All(char.IsAsciiLetterUpper);
}

internal enum DocumentAccessAction
{
    Stored,
    Read,
    Shredded,
}

/// <summary>An append-only record of who touched a travel document, and why (security rules: Sensitive PII access is audited).</summary>
internal sealed record DocumentAccess(Guid DocumentId, Guid OrderId, DocumentAccessAction Action, string Actor, DateTimeOffset At, string? CorrelationId)
{
    public long Id { get; private set; }
}

internal enum RetentionAction
{
    LegalHoldPlaced,
    LegalHoldReleased,
    Anonymised,
    DocumentsShredded,
}

/// <summary>An append-only record of retention actions on an order's personal data, with the actor and reason (audit).</summary>
internal sealed record RetentionEvent(Guid OrderId, RetentionAction Action, string Actor, string Reason, DateTimeOffset At, string? CorrelationId)
{
    public long Id { get; private set; }
}
