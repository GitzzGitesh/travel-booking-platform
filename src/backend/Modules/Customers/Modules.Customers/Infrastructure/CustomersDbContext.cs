using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Infrastructure;

/// <summary>The Customers module's own schema (ADR 0002, database rules): no other module reads or writes it.</summary>
internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public const string Schema = "customers";
    public const string ConnectionStringName = "Customers";

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("Customers");
        customer.HasKey(c => c.Id);
        customer.Property(c => c.Id).ValueGeneratedNever();
        // Compared exactly: case, trailing spaces and accents never make two accounts equal (binary collation).
        customer.Property(c => c.IdentityIssuer).HasMaxLength(Customer.MaxIssuerLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        customer.Property(c => c.IdentitySubject).HasMaxLength(Customer.MaxSubjectLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");

        // One customer per identity-provider account, enforced by the database.
        customer.HasIndex(c => new { c.IdentityIssuer, c.IdentitySubject }).IsUnique();
        customer.Ignore(c => c.CustomerId);

        modelBuilder.AddInbox().AddJobLeases(); // ADR 0007: consumed events (abandoned orders), and the purge job's lease
        modelBuilder.AddAuditLog(); // ADR 0022: staff actions on personal data (legal holds), saved with the action
        MapPersonalData(modelBuilder);
    }

    // Personal data (Q9, ADR 0020): kept only here, keyed by internal ids, with no foreign key to other schemas.
    private static void MapPersonalData(ModelBuilder modelBuilder)
    {
        var set = modelBuilder.Entity<OrderTravellerSet>();
        set.ToTable("TravellerSets");
        set.HasKey(s => s.OrderId);
        set.Property(s => s.OrderId).ValueGeneratedNever();
        set.Property(s => s.CustomerId).HasMaxLength(64).IsUnicode(false);
        set.Property(s => s.ContactEmail).HasMaxLength(OrderTravellerSet.MaxEmailLength);
        set.Property(s => s.ContactPhone).HasMaxLength(16).IsUnicode(false);
        set.Property<byte[]>("RowVersion").IsRowVersion();
        set.Ignore(s => s.IsFrozen);
        set.HasIndex(s => new { s.LegalHold, s.RetainUntil }); // the purge's work list
        set.HasIndex(s => new { s.LegalHold, s.DocumentsRetainUntil });
        set.HasMany(s => s.Travellers).WithOne().HasForeignKey("OrderId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        set.Navigation(s => s.Travellers).HasField("_travellers").UsePropertyAccessMode(PropertyAccessMode.Field);

        var traveller = modelBuilder.Entity<Traveller>();
        traveller.ToTable("Travellers");
        traveller.HasKey(t => t.Id);
        traveller.Property(t => t.Id).ValueGeneratedNever();
        traveller.Property(t => t.Kind).HasConversion<string>().HasMaxLength(10);
        traveller.Property(t => t.Gender).HasConversion<string>().HasMaxLength(10);
        traveller.Property(t => t.GivenNames).HasMaxLength(OrderTravellerSet.MaxNameLength);
        traveller.Property(t => t.Surname).HasMaxLength(OrderTravellerSet.MaxNameLength);
        traveller.Ignore(t => t.Details);
        traveller.HasIndex("OrderId", nameof(Traveller.Position)).IsUnique(); // never a traveller outside a set, one per position

        var document = modelBuilder.Entity<TravelDocument>();
        document.ToTable("TravelDocuments");
        document.HasKey(d => d.Id);
        document.Property(d => d.Id).ValueGeneratedNever();
        document.Property(d => d.KeyId).HasMaxLength(50).IsUnicode(false);
        document.Property(d => d.WrappedKey).HasMaxLength(128);
        document.Property(d => d.Ciphertext).HasMaxLength(1024);
        document.Ignore(d => d.IsShredded);
        document.HasIndex(d => new { d.OrderId, d.ShreddedAt });

        var access = modelBuilder.Entity<DocumentAccess>();
        access.ToTable("DocumentAccessLog"); // append-only
        access.HasKey(a => a.Id);
        access.Property(a => a.Id).UseIdentityColumn();
        access.Property(a => a.Action).HasConversion<string>().HasMaxLength(20);
        access.Property(a => a.Actor).HasMaxLength(100);
        access.Property(a => a.CorrelationId).HasMaxLength(100);
        access.HasIndex(a => new { a.DocumentId, a.Id });

        var retention = modelBuilder.Entity<RetentionEvent>();
        retention.ToTable("RetentionEvents"); // append-only
        retention.HasKey(r => r.Id);
        retention.Property(r => r.Id).UseIdentityColumn();
        retention.Property(r => r.Action).HasConversion<string>().HasMaxLength(30);
        retention.Property(r => r.Actor).HasMaxLength(100);
        retention.Property(r => r.Reason).HasMaxLength(250);
        retention.Property(r => r.CorrelationId).HasMaxLength(100);
        retention.HasIndex(r => new { r.OrderId, r.Id });
    }
}

internal sealed class SqlPersonalDataStore(CustomersDbContext db) : IPersonalDataStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<OrderTravellerSet?> FindSetAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Set<OrderTravellerSet>().Include(s => s.Travellers).SingleOrDefaultAsync(s => s.OrderId == orderId, cancellationToken);

    public void AddSet(OrderTravellerSet set) => db.Set<OrderTravellerSet>().Add(set);

    public Task<TravelDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.Set<TravelDocument>().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public async Task<IReadOnlyList<TravelDocument>> FindLiveDocumentsAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<TravelDocument>().Where(d => d.OrderId == orderId && d.ShreddedAt == null).ToListAsync(cancellationToken);

    public void AddDocument(TravelDocument document) => db.Set<TravelDocument>().Add(document);

    public void Audit(DocumentAccess access) => db.Set<DocumentAccess>().Add(access);

    public void Audit(RetentionEvent retentionEvent) => db.Set<RetentionEvent>().Add(retentionEvent);

    public void Audit(BuildingBlocks.Audit.AuditEntry entry) => db.Set<BuildingBlocks.Audit.AuditEntry>().Add(entry);

    public Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken) =>
        db.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId && m.Handler == handler, cancellationToken);

    public void MarkConsumed(Guid messageId, string handler, DateTimeOffset at) =>
        db.Set<InboxMessage>().Add(InboxMessage.For(messageId, handler, at));

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<Guid>> FindDueForPurgeAsync(DateOnly today, int limit, CancellationToken cancellationToken) =>
        await db.Set<OrderTravellerSet>().AsNoTracking()
            .Where(s => !s.LegalHold
                && ((s.AnonymisedAt == null && s.RetainUntil < today)
                    || (s.DocumentsRetainUntil < today && db.Set<TravelDocument>().Any(d => d.OrderId == s.OrderId && d.ShreddedAt == null))))
            .OrderBy(s => s.DocumentsRetainUntil)
            .Select(s => s.OrderId)
            .Take(limit)
            .ToListAsync(cancellationToken);
}

internal sealed class SqlCustomerStore(CustomersDbContext db) : ICustomerStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<Customer?> FindByIdentityAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.IdentityIssuer == issuer && c.IdentitySubject == subject, cancellationToken);

    public async Task<bool> TryAddAsync(Customer customer, CancellationToken cancellationToken)
    {
        db.Customers.Add(customer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules).</summary>
internal sealed class CustomersDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", CustomersDbContext.Schema))
            .Options);
}
