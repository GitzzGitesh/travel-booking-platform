using System.Net.Mail;
using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Customers.Domain;

internal enum PassengerKind
{
    Adult,
    Child,
    Infant,
}

/// <summary>As airlines take it. Other values follow when suppliers accept them.</summary>
internal enum TravellerGender
{
    Female,
    Male,
}

internal enum TravellerSetError
{
    /// <summary>The travellers do not match the order's passenger mix.</summary>
    WrongPassengers,

    /// <summary>A name, date of birth, gender or contact detail is not acceptable.</summary>
    InvalidDetails,

    /// <summary>Under a legal hold, or already anonymised: personal data is not changed.</summary>
    Frozen,
}

/// <summary>
/// The personal data an order needs (Q9, ADR 0020): the booker's contact and one traveller per passenger (legal name as
/// on the travel document, date of birth, gender), kept only in the Customers schema, keyed by our internal ids. Nothing
/// else is collected: no meal or medical requests, frequent-flyer numbers or addresses. It is anonymised after its
/// retention date (the last travel date plus the retention period) unless a legal hold stops it; the order and financial
/// records elsewhere only ever hold the internal ids.
/// </summary>
internal sealed class OrderTravellerSet
{
    public const int MaxNameLength = 60;
    public const int MaxEmailLength = 254;

    private readonly List<Traveller> _travellers = [];

    private OrderTravellerSet()
    {
    }

    /// <summary>The Orders order these travellers belong to (an id only: no foreign key across schemas).</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Our internal customer id, the order's owner.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    public IReadOnlyList<Traveller> Travellers => _travellers;

    /// <summary>The last travel date: every retention date counts from it.</summary>
    public DateOnly LastTravelDate { get; private set; }

    /// <summary>After this date the travellers' and booker's personal data is anonymised (unless on legal hold).</summary>
    public DateOnly RetainUntil { get; private set; }

    /// <summary>After this date the travel documents are crypto-shredded (unless on legal hold).</summary>
    public DateOnly DocumentsRetainUntil { get; private set; }

    public DateTimeOffset? AnonymisedAt { get; private set; }

    /// <summary>A legal hold (dispute, chargeback, fraud or legal case) suspends anonymisation and shredding.</summary>
    public bool LegalHold { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; }

    public bool IsFrozen => LegalHold || AnonymisedAt is not null;

    public static OrderTravellerSet Start(Guid orderId, string customerId, DateTimeOffset at) =>
        new() { OrderId = orderId, CustomerId = customerId, UpdatedAt = at };

    /// <summary>
    /// Replaces the contact and travellers. A traveller whose name, date of birth or gender is unchanged keeps their
    /// document; any other document is dropped (it would no longer match the traveller) and must be given again.
    /// </summary>
    /// <returns>The documents to shred, or an error.</returns>
    public Result<IReadOnlyList<Guid>, TravellerSetError> Replace(
        string email, string phone, IReadOnlyList<TravellerDetails> travellers, PassengerCounts expected, RetentionDates retention, DateTimeOffset at)
    {
        if (IsFrozen)
        {
            return Result<IReadOnlyList<Guid>, TravellerSetError>.Failure(TravellerSetError.Frozen);
        }

        if (travellers.Count(t => t.Kind == PassengerKind.Adult) != expected.Adults
            || travellers.Count(t => t.Kind == PassengerKind.Child) != expected.Children
            || travellers.Count(t => t.Kind == PassengerKind.Infant) != expected.Infants)
        {
            return Result<IReadOnlyList<Guid>, TravellerSetError>.Failure(TravellerSetError.WrongPassengers);
        }

        if (!IsValidEmail(email) || !IsValidPhone(phone) || travellers.Any(t => !t.IsValid(retention.LastTravelDate)))
        {
            return Result<IReadOnlyList<Guid>, TravellerSetError>.Failure(TravellerSetError.InvalidDetails);
        }

        var dropped = new List<Guid>();
        var next = new List<Traveller>();
        for (var position = 0; position < travellers.Count; position++)
        {
            var details = travellers[position];
            var existing = _travellers.SingleOrDefault(t => t.Position == position);
            if (existing is not null && existing.Details == details)
            {
                next.Add(existing);
                continue;
            }

            if (existing?.DocumentId is { } document)
            {
                dropped.Add(document);
            }

            next.Add(new Traveller(Guid.NewGuid(), position, details));
        }

        dropped.AddRange(_travellers.Where(t => t.Position >= travellers.Count && t.DocumentId is not null).Select(t => t.DocumentId!.Value));
        _travellers.Clear();
        _travellers.AddRange(next);
        ContactEmail = email;
        ContactPhone = phone;
        ApplyRetention(retention);
        Touch(at);
        return Result<IReadOnlyList<Guid>, TravellerSetError>.Success(dropped);
    }

    /// <summary>Links a stored document to the traveller at <paramref name="position"/>; <paramref name="replaced"/> is the document it replaces, if any.</summary>
    /// <returns>Null when attached; otherwise why not.</returns>
    public TravellerSetError? AttachDocument(int position, Guid documentId, DateTimeOffset at, out Guid? replaced)
    {
        replaced = null;
        if (IsFrozen || _travellers.SingleOrDefault(t => t.Position == position) is not { } traveller)
        {
            return IsFrozen ? TravellerSetError.Frozen : TravellerSetError.WrongPassengers;
        }

        replaced = traveller.DocumentId;
        traveller.DocumentId = documentId;
        Touch(at);
        return null;
    }

    /// <summary>The travel dates can move (a new offer at revalidation); retention follows the latest.</summary>
    public void ApplyRetention(RetentionDates retention)
    {
        LastTravelDate = retention.LastTravelDate;
        RetainUntil = retention.PersonalDataUntil;
        DocumentsRetainUntil = retention.DocumentsUntil;
    }

    /// <summary>
    /// The order was abandoned before booking, with no payment unsettled (Q9, approved 2026-09-29): its documents are due
    /// at once (the retention dates are the last day kept), and the rest of the personal data after a grace period. Only
    /// ever shortens retention. A legal hold still blocks the purge. Returns whether anything changed.
    /// </summary>
    public bool OrderAbandoned(DateOnly abandonedOn, int graceDays, DateTimeOffset at)
    {
        var documentsUntil = abandonedOn.AddDays(-1);
        var personalDataUntil = abandonedOn.AddDays(graceDays);
        if (DocumentsRetainUntil <= documentsUntil && RetainUntil <= personalDataUntil)
        {
            return false;
        }

        DocumentsRetainUntil = DocumentsRetainUntil < documentsUntil ? DocumentsRetainUntil : documentsUntil;
        RetainUntil = RetainUntil < personalDataUntil ? RetainUntil : personalDataUntil;
        Touch(at);
        return true;
    }

    public void PlaceLegalHold(DateTimeOffset at)
    {
        LegalHold = true;
        Touch(at);
    }

    public void ReleaseLegalHold(DateTimeOffset at)
    {
        LegalHold = false;
        Touch(at);
    }

    /// <summary>
    /// Removes the personal data for good: names, dates of birth, genders and the contact. The travellers keep their
    /// passenger type and position, so booking and financial records still add up. Returns the documents to shred.
    /// </summary>
    public IReadOnlyList<Guid> Anonymise(DateTimeOffset at)
    {
        var documents = _travellers.Where(t => t.DocumentId is not null).Select(t => t.DocumentId!.Value).ToList();
        foreach (var traveller in _travellers)
        {
            traveller.Anonymise();
        }

        ContactEmail = null;
        ContactPhone = null;
        AnonymisedAt = at;
        Touch(at);
        return documents;
    }

    /// <summary>
    /// Records that the travellers' documents were shredded (retention): they are no longer provided. Changes the set, so
    /// its row version guards the purge against a legal hold placed meanwhile.
    /// </summary>
    public void DocumentsShredded(DateTimeOffset at)
    {
        foreach (var traveller in _travellers)
        {
            traveller.DocumentId = null;
        }

        Touch(at);
    }

    public static bool IsValidEmail(string? email) =>
        email is { Length: > 0 and <= MaxEmailLength } && !email.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;

    /// <summary>E.164: a plus sign and 7 to 15 digits.</summary>
    public static bool IsValidPhone(string? phone) =>
        phone is { Length: >= 8 and <= 16 } && phone[0] == '+' && phone[1] is >= '1' and <= '9' && phone[1..].All(char.IsAsciiDigit);

    private void Touch(DateTimeOffset at)
    {
        UpdatedAt = at;
        Revision++;
    }
}

/// <summary>One traveller: their position in the order, and their details until anonymised.</summary>
internal sealed class Traveller
{
    private Traveller()
    {
    }

    public Traveller(Guid id, int position, TravellerDetails details)
    {
        Id = id;
        Position = position;
        Kind = details.Kind;
        GivenNames = details.GivenNames;
        Surname = details.Surname;
        DateOfBirth = details.DateOfBirth;
        Gender = details.Gender;
    }

    public Guid Id { get; private set; }

    public int Position { get; private set; }

    public PassengerKind Kind { get; private set; }

    public string? GivenNames { get; private set; }

    public string? Surname { get; private set; }

    public DateOnly? DateOfBirth { get; private set; }

    public TravellerGender? Gender { get; private set; }

    /// <summary>The stored travel document, when the supplier requires one.</summary>
    public Guid? DocumentId { get; internal set; }

    public TravellerDetails? Details => GivenNames is not null && Surname is not null && DateOfBirth is { } born && Gender is { } gender
        ? new TravellerDetails(Kind, GivenNames, Surname, born, gender)
        : null;

    internal void Anonymise()
    {
        GivenNames = null;
        Surname = null;
        DateOfBirth = null;
        Gender = null;
        DocumentId = null;
    }
}

/// <summary>A traveller's details as airlines need them: the legal name as on the travel document, date of birth, gender.</summary>
internal sealed record TravellerDetails(PassengerKind Kind, string GivenNames, string Surname, DateOnly DateOfBirth, TravellerGender Gender)
{
    /// <summary>Never the personal data: a record's generated ToString would print it into any log or exception.</summary>
    public override string ToString() => $"TravellerDetails {{ Kind = {Kind} }}";

    /// <summary>
    /// Names in Latin letters (with spaces, hyphens and apostrophes), as airline systems take them. The age must fit the
    /// passenger type on the last travel date (IATA: infants under 2, children 2 to 11, adults 12 and over); the supplier
    /// makes the final check.
    /// </summary>
    public bool IsValid(DateOnly lastTravelDate)
    {
        if (!IsName(GivenNames) || !IsName(Surname) || !Enum.IsDefined(Kind) || !Enum.IsDefined(Gender)
            || DateOfBirth.Year < 1900 || DateOfBirth > lastTravelDate)
        {
            return false;
        }

        var age = lastTravelDate.Year - DateOfBirth.Year;
        if (DateOfBirth.AddYears(age) > lastTravelDate)
        {
            age--;
        }
        return Kind switch
        {
            PassengerKind.Infant => age < 2,
            PassengerKind.Child => age is >= 2 and < 12,
            _ => age >= 12,
        };
    }

    private static bool IsName(string name) =>
        name is { Length: > 0 and <= OrderTravellerSet.MaxNameLength } && name.Trim() == name
        && name.All(c => char.IsAsciiLetter(c) || c is ' ' or '-' or '\'') && name.Any(char.IsAsciiLetter);
}

/// <summary>The passenger mix travellers are given for.</summary>
internal sealed record PassengerCounts(int Adults, int Children, int Infants);

/// <summary>When an order's personal data and documents are removed (Q9: counted from the last travel date).</summary>
internal sealed record RetentionDates(DateOnly LastTravelDate, DateOnly PersonalDataUntil, DateOnly DocumentsUntil);
