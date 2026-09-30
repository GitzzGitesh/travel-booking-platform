using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Domain;
using TravelBooking.Modules.Customers.Infrastructure;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Customers.UnitTests;

/// <summary>
/// Q9 personal data: what is accepted, how documents are encrypted and shredded, and how retention and legal holds
/// apply (ADR 0020).
/// </summary>
public sealed class PersonalDataTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _lastTravel = new(2027, 3, 10);
    private static readonly Guid _orderId = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly StubOrders _orders = new();

    // ---------- Traveller details ----------

    [Theory]
    [InlineData("Ada", "Lovelace", "1990-12-10", PassengerKind.Adult, true)]
    [InlineData("Mary-Jane", "O'Neil Smith", "2015-03-11", PassengerKind.Child, true)] // 11 on the last travel date
    [InlineData("Tom", "Baby", "2025-03-11", PassengerKind.Infant, true)] // under 2 on the last travel date
    [InlineData("Tom", "Baby", "2025-03-10", PassengerKind.Infant, false)] // turns 2 on the last travel date
    [InlineData("Ada", "Lovelace", "2016-01-01", PassengerKind.Adult, false)] // too young for an adult
    [InlineData("Ådå", "Lovelace", "1990-12-10", PassengerKind.Adult, false)] // Latin letters only, as on airline systems
    [InlineData(" Ada", "Lovelace", "1990-12-10", PassengerKind.Adult, false)]
    [InlineData("Ada", "Lovelace", "1899-12-31", PassengerKind.Adult, false)]
    [InlineData("Ada", "Lovelace", "2027-03-11", PassengerKind.Adult, false)] // born after travelling
    internal void Traveller_details_are_checked_against_the_passenger_type_on_the_last_travel_date(
        string given, string surname, string born, PassengerKind kind, bool valid) =>
        new TravellerDetails(kind, given, surname, DateOnly.Parse(born, System.Globalization.CultureInfo.InvariantCulture), TravellerGender.Female)
            .IsValid(_lastTravel).ShouldBe(valid);

    [Theory]
    [InlineData("ada@example.com", "+447700900123", true)]
    [InlineData("ada@example", "+447700900123", true)]
    [InlineData("not an email", "+447700900123", false)]
    [InlineData("ada@example.com", "07700900123", false)] // E.164 only
    [InlineData("ada@example.com", "+0770090012", false)]
    [InlineData("ada@example.com", "+1234567890123456", false)] // more than 15 digits
    public void The_contact_is_an_email_and_an_E164_phone(string email, string phone, bool valid) =>
        (OrderTravellerSet.IsValidEmail(email) && OrderTravellerSet.IsValidPhone(phone)).ShouldBe(valid);

    [Fact]
    public async Task Travellers_are_saved_for_exactly_the_orders_passengers_with_retention_from_the_last_travel_date()
    {
        _orders.Needs = Needs(adults: 1, children: 1);

        var wrong = await Save([Adult()]);
        var saved = await Save([Adult(), Child()]);

        wrong.Error.ShouldBe(TravellersFailure.WrongPassengers);
        var set = saved.Value;
        set.Travellers.Count.ShouldBe(2);
        (set.RetainUntil, set.DocumentsRetainUntil).ShouldBe((_lastTravel.AddMonths(25), _lastTravel.AddDays(30)));
    }

    [Fact]
    public async Task Only_the_customers_own_order_awaiting_payment_takes_travellers()
    {
        _orders.Needs = null; // another customer's order, or none
        (await Save([Adult()])).Error.ShouldBe(TravellersFailure.OrderNotFound);

        _orders.Needs = Needs() with { Editable = false }; // paid already
        (await Save([Adult()])).Error.ShouldBe(TravellersFailure.NotEditable);
    }

    // ---------- Documents ----------

    [Fact]
    public async Task No_document_is_collected_unless_the_supplier_requires_one()
    {
        _orders.Needs = Needs(documentsRequired: false);
        await Save([Adult()]);

        (await SaveDocument(0)).Error.ShouldBe(TravellersFailure.DocumentsNotRequired);
        _store.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_is_stored_only_as_ciphertext_audited_and_readable_back_with_an_audited_read()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);

        (await SaveDocument(0)).IsSuccess.ShouldBeTrue();

        var stored = _store.Documents.ShouldHaveSingleItem();
        Encoding.UTF8.GetString(stored.Ciphertext!).ShouldNotContain("P1234567");
        Encoding.UTF8.GetString(stored.WrappedKey!).ShouldNotContain("P1234567");
        _store.Accesses.ShouldHaveSingleItem().Action.ShouldBe(DocumentAccessAction.Stored);

        var read = await new TravelDocumentReader(_store, Protector(), _clock).ReadAsync(_orderId, 0, "system:flight-booking", "trace-1", Ct);

        read.Value.ShouldBe(Document());
        _store.Accesses[^1].ShouldSatisfyAllConditions(a => a.Action.ShouldBe(DocumentAccessAction.Read), a => a.Actor.ShouldBe("system:flight-booking"));
    }

    [Fact]
    public async Task Without_an_encryption_key_no_document_is_stored_at_all()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);

        var result = await new SaveTravelDocumentHandler(_orders, _store, Protector(configured: false), _clock)
            .HandleAsync(new SaveTravelDocument(_orderId, "cust-1", 0, Document(), null), Ct);

        result.Error.ShouldBe(TravellersFailure.DocumentsUnavailable);
        _store.Documents.ShouldBeEmpty();
    }

    [Fact]
    public void A_ciphertext_moved_to_another_document_or_tampered_with_does_not_decrypt()
    {
        var protector = Protector();
        var id = Guid.NewGuid();
        var sealedDocument = protector.Protect(id, Document()).Value;
        var tampered = sealedDocument with { Ciphertext = [.. sealedDocument.Ciphertext] };
        tampered.Ciphertext[^1] ^= 0x01;

        protector.Unprotect(id, sealedDocument).Value.ShouldBe(Document());
        protector.Unprotect(Guid.NewGuid(), sealedDocument).Error.ShouldBe(DocumentProtectionError.Unreadable);
        protector.Unprotect(id, tampered).Error.ShouldBe(DocumentProtectionError.Unreadable);
    }

    [Fact]
    public async Task Changing_a_travellers_details_shreds_their_document_and_keeps_the_others()
    {
        _orders.Needs = Needs(adults: 2, documentsRequired: true);
        await Save([Adult(), Adult("Grace", "Hopper")]);
        await SaveDocument(0);
        await SaveDocument(1);

        await Save([Adult(), Adult("Grace", "Murray")]);

        var set = _store.Sets.Single();
        set.Travellers[0].DocumentId.ShouldNotBeNull();
        set.Travellers[1].DocumentId.ShouldBeNull();
        _store.Documents.Count(d => d.IsShredded).ShouldBe(1);
        _store.Documents.Single(d => d.IsShredded).ShouldSatisfyAllConditions(d => d.WrappedKey.ShouldBeNull(), d => d.Ciphertext.ShouldBeNull());
    }

    [Fact]
    public void Personal_data_is_never_printed()
    {
        Document().ToString().ShouldNotContain("P1234567");
        Adult().ToString().ShouldNotContain("Lovelace");
        new SaveOrderTravellers(_orderId, "cust-1", "ada@example.com", "+447700900123", [Adult()]).ToString().ShouldSatisfyAllConditions(
            text => text.ShouldNotContain("ada@"), text => text.ShouldNotContain("+44"), text => text.ShouldNotContain("Lovelace"));
    }

    // ---------- Retention and legal hold ----------

    [Fact]
    public async Task Documents_are_shredded_after_30_days_and_personal_data_anonymised_after_25_months()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);
        await SaveDocument(0);
        var purger = new PersonalDataPurger(_store, _clock);

        _clock.SetUtcNow(new DateTimeOffset(_lastTravel.AddDays(30).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        (await purger.PurgeAsync(_orderId, Ct)).ShouldBeFalse(); // not past the date yet

        _clock.SetUtcNow(new DateTimeOffset(_lastTravel.AddDays(31).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        (await purger.PurgeAsync(_orderId, Ct)).ShouldBeTrue();
        _store.Documents.Single().IsShredded.ShouldBeTrue();
        _store.Sets.Single().Travellers[0].GivenNames.ShouldBe("Ada"); // personal data still kept
        _store.Sets.Single().Travellers[0].DocumentId.ShouldBeNull(); // no longer provided
        (await new OrderTravellersReadinessQuery(_store, new TravelDocumentReader(_store, Protector(), _clock)).GetReadinessAsync(_orderId, "cust-1", Ct)).DocumentsProvided.ShouldBe(0);

        _clock.SetUtcNow(new DateTimeOffset(_lastTravel.AddMonths(25).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        (await purger.PurgeAsync(_orderId, Ct)).ShouldBeTrue();
        var set = _store.Sets.Single();
        (set.ContactEmail, set.ContactPhone, set.Travellers[0].GivenNames, set.Travellers[0].DateOfBirth).ShouldBe((null, null, null, null));
        set.Travellers[0].Kind.ShouldBe(PassengerKind.Adult); // booking records still add up
        _store.Events.Select(e => e.Action).ShouldBe([RetentionAction.DocumentsShredded, RetentionAction.Anonymised]);
        (await purger.PurgeAsync(_orderId, Ct)).ShouldBeFalse(); // idempotent
    }

    [Fact]
    public async Task A_legal_hold_stops_the_purge_and_changes_until_it_is_released()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);
        await SaveDocument(0);
        var holds = new LegalHoldHandler(_store, _clock);

        (await holds.HandleAsync(new LegalHoldRequest(_orderId, Hold: true, "ops-1", "chargeback case 123"), Ct)).ShouldBe(LegalHoldOutcome.Applied);
        (await holds.HandleAsync(new LegalHoldRequest(_orderId, Hold: true, "ops-1", "chargeback case 123"), Ct)).ShouldBe(LegalHoldOutcome.Unchanged);
        _clock.SetUtcNow(new DateTimeOffset(_lastTravel.AddYears(3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        (await new PersonalDataPurger(_store, _clock).PurgeAsync(_orderId, Ct)).ShouldBeFalse();
        _store.Documents.Single().IsShredded.ShouldBeFalse();
        (await Save([Adult("Grace")])).Error.ShouldBe(TravellersFailure.NotEditable); // frozen while held

        (await holds.HandleAsync(new LegalHoldRequest(_orderId, Hold: false, "ops-1", "case closed"), Ct)).ShouldBe(LegalHoldOutcome.Applied);
        (await new PersonalDataPurger(_store, _clock).PurgeAsync(_orderId, Ct)).ShouldBeTrue();
        _store.Events.Select(e => (e.Action, e.Actor)).Take(2).ShouldBe([(RetentionAction.LegalHoldPlaced, "operator:ops-1"), (RetentionAction.LegalHoldReleased, "operator:ops-1")]);
    }

    // ---------- Abandoned orders (Q9, approved 2026-09-29) ----------

    [Fact]
    public async Task An_abandoned_orders_documents_are_shredded_at_once_and_its_personal_data_anonymised_30_days_later()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);
        await SaveDocument(0);
        var abandoned = Abandoned();

        await AbandonedHandler().HandleAsync(abandoned, Ct);
        await AbandonedHandler().HandleAsync(abandoned, Ct); // redelivered: consumed once

        var set = _store.Sets.Single();
        (set.DocumentsRetainUntil, set.RetainUntil).ShouldBe((Today.AddDays(-1), Today.AddDays(30)));
        _store.Documents.Single().IsShredded.ShouldBeTrue();
        set.Travellers[0].GivenNames.ShouldBe("Ada"); // kept during the grace period
        _store.Events.Select(e => e.Action).ShouldBe([RetentionAction.ShortenedForAbandonedOrder, RetentionAction.DocumentsShredded]);

        _clock.Advance(TimeSpan.FromDays(30));
        (await new PersonalDataPurger(_store, _clock).PurgeAsync(_orderId, Ct)).ShouldBeFalse(); // the 30th day is still kept
        _clock.Advance(TimeSpan.FromDays(1));
        (await new PersonalDataPurger(_store, _clock).PurgeAsync(_orderId, Ct)).ShouldBeTrue();
        _store.Sets.Single().AnonymisedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_legal_hold_keeps_an_abandoned_orders_data_until_released()
    {
        _orders.Needs = Needs(documentsRequired: true);
        await Save([Adult()]);
        await SaveDocument(0);
        await new LegalHoldHandler(_store, _clock).HandleAsync(new LegalHoldRequest(_orderId, true, "ops-1", "case 42"), Ct);

        await AbandonedHandler().HandleAsync(Abandoned(), Ct);
        _clock.Advance(TimeSpan.FromDays(60));
        await new PersonalDataPurger(_store, _clock).PurgeAsync(_orderId, Ct);

        _store.Documents.Single().IsShredded.ShouldBeFalse();
        _store.Sets.Single().AnonymisedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Abandonment_only_ever_shortens_retention_and_needs_no_travellers()
    {
        // Travelled long ago: the normal retention already ends within the grace period.
        _orders.Needs = Needs() with { LastTravelDate = Today.AddMonths(-25).AddDays(10) };
        await Save([Adult()]);
        var before = (_store.Sets.Single().RetainUntil, _store.Sets.Single().DocumentsRetainUntil);

        await AbandonedHandler().HandleAsync(Abandoned(), Ct);
        await AbandonedHandler().HandleAsync(Abandoned(Guid.NewGuid()), Ct); // an order with no travellers: nothing to do

        (_store.Sets.Single().RetainUntil, _store.Sets.Single().DocumentsRetainUntil).ShouldBe(before);
        _store.Events.ShouldBeEmpty();
        _store.Consumed.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("ops 1", "reason")]
    [InlineData("ops-1", " ")]
    [InlineData("ops-1", "line\nbreak")]
    public async Task A_legal_hold_names_its_operator_and_reason(string actor, string reason)
    {
        _orders.Needs = Needs();
        await Save([Adult()]);

        (await new LegalHoldHandler(_store, _clock).HandleAsync(new LegalHoldRequest(_orderId, true, actor, reason), Ct)).ShouldBe(LegalHoldOutcome.Invalid);
    }

    // ---------- Helpers ----------

    private Task<BuildingBlocks.Result<OrderTravellerSet, TravellersFailure>> Save(IReadOnlyList<TravellerDetails> travellers) =>
        new SaveOrderTravellersHandler(_orders, _store, _clock, Options.Create(new PersonalDataRetentionOptions()))
            .HandleAsync(new SaveOrderTravellers(_orderId, "cust-1", "ada@example.com", "+447700900123", travellers), Ct);

    private Task<BuildingBlocks.Result<bool, TravellersFailure>> SaveDocument(int position) =>
        new SaveTravelDocumentHandler(_orders, _store, Protector(), _clock).HandleAsync(new SaveTravelDocument(_orderId, "cust-1", position, Document(), "trace-0"), Ct);

    private static OrderTravellerNeeds Needs(int adults = 1, int children = 0, bool documentsRequired = false) =>
        new(_orderId, adults, children, 0, documentsRequired, _lastTravel, Editable: true);

    private static TravellerDetails Adult(string given = "Ada", string surname = "Lovelace") =>
        new(PassengerKind.Adult, given, surname, new DateOnly(1990, 12, 10), TravellerGender.Female);

    private static TravellerDetails Child() => new(PassengerKind.Child, "Byron", "Lovelace", new DateOnly(2018, 5, 1), TravellerGender.Male);

    private static TravelDocumentDetails Document() => new(TravelDocumentType.Passport, "P1234567", "GB", "GB", new DateOnly(2030, 1, 1));

    private static DateOnly Today => DateOnly.FromDateTime(_now.UtcDateTime);

    private static TravelBooking.Modules.Orders.Contracts.OrderAbandoned Abandoned(Guid? orderId = null) =>
        new(orderId is null ? _abandonedEvent : Guid.NewGuid(), _now, orderId ?? _orderId, "trace-abandon");

    private static readonly Guid _abandonedEvent = Guid.NewGuid();

    private OrderAbandonedHandler AbandonedHandler() =>
        new(_store, new PersonalDataPurger(_store, _clock), _clock, Options.Create(new PersonalDataRetentionOptions()));

    private static readonly string _testKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmDocumentProtector Protector(bool configured = true) => new(new StaticOptions(configured
        ? new DocumentEncryptionOptions { ActiveKeyId = "k1", Keys = { ["k1"] = _testKey } }
        : new DocumentEncryptionOptions()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StaticOptions(DocumentEncryptionOptions value) : IOptionsMonitor<DocumentEncryptionOptions>
    {
        public DocumentEncryptionOptions CurrentValue => value;

        public DocumentEncryptionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<DocumentEncryptionOptions, string?> listener) => null;
    }

    private sealed class StubOrders : IOrderTravellerNeeds
    {
        public OrderTravellerNeeds? Needs { get; set; }

        public Task<OrderTravellerNeeds?> FindAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
            Task.FromResult(customerId == "cust-1" ? Needs : null);
    }

    private sealed class FakeStore : IPersonalDataStore
    {
        public List<OrderTravellerSet> Sets { get; } = [];

        public List<TravelDocument> Documents { get; } = [];

        public List<DocumentAccess> Accesses { get; } = [];

        public List<RetentionEvent> Events { get; } = [];

        public Task<OrderTravellerSet?> FindSetAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Sets.SingleOrDefault(s => s.OrderId == orderId));

        public void AddSet(OrderTravellerSet set) => Sets.Add(set);

        public Task<TravelDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(Documents.SingleOrDefault(d => d.Id == documentId));

        public Task<IReadOnlyList<TravelDocument>> FindLiveDocumentsAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TravelDocument>>([.. Documents.Where(d => d.OrderId == orderId && !d.IsShredded)]);

        public void AddDocument(TravelDocument document) => Documents.Add(document);

        public void Audit(DocumentAccess access) => Accesses.Add(access);

        public void Audit(RetentionEvent retentionEvent) => Events.Add(retentionEvent);

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FindDueForPurgeAsync(DateOnly today, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([.. Sets.Select(s => s.OrderId)]);

        public HashSet<(Guid, string)> Consumed { get; } = [];

        public Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken) =>
            Task.FromResult(Consumed.Contains((messageId, handler)));

        public void MarkConsumed(Guid messageId, string handler, DateTimeOffset at) => Consumed.Add((messageId, handler));
    }
}
