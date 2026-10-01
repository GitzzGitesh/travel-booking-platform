using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Access.Application;
using TravelBooking.Modules.Access.Domain;

namespace TravelBooking.Modules.Access.Infrastructure;

/// <summary>The Access module's own schema (ADR 0002, ADR 0022): no other module reads or writes it.</summary>
internal sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public const string Schema = "access";
    public const string ConnectionStringName = "Access";

    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var member = modelBuilder.Entity<StaffMember>();
        member.ToTable("StaffMembers");
        member.HasKey(m => m.Id);
        member.Property(m => m.Id).ValueGeneratedNever();
        // Compared exactly: case, trailing spaces and accents never make two accounts equal (binary collation).
        member.Property(m => m.IdentityIssuer).HasMaxLength(StaffMember.MaxIssuerLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        member.Property(m => m.IdentityObjectId).HasMaxLength(StaffMember.MaxObjectIdLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        member.HasIndex(m => new { m.IdentityIssuer, m.IdentityObjectId }).IsUnique(); // one staff member per account
        member.Ignore(m => m.StaffId);

        // Managed role grants (ADR 0022): requests with maker-checker, and the grants approved requests created.
        var request = modelBuilder.Entity<RoleChangeRequest>();
        request.ToTable("RoleChangeRequests");
        request.HasKey(r => r.Id);
        request.Property(r => r.Id).ValueGeneratedNever();
        request.Property(r => r.ObjectId).HasMaxLength(StaffMember.MaxObjectIdLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        request.Property(r => r.RequestedByObjectId).HasMaxLength(StaffMember.MaxObjectIdLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        request.Property(r => r.Role).HasMaxLength(50).IsUnicode(false);
        request.Property(r => r.Kind).HasConversion<string>().HasMaxLength(10);
        request.Property(r => r.Status).HasConversion<string>().HasMaxLength(10);
        request.Property(r => r.Reason).HasMaxLength(AuditReasons.MaxLength);
        request.Property(r => r.DecisionReason).HasMaxLength(AuditReasons.MaxLength);
        request.Property(r => r.RequestedBy).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.DecidedBy).HasMaxLength(64).IsUnicode(false);
        request.Property<byte[]>("RowVersion").IsRowVersion();
        // One request at a time per account and role: a second one waits for the first decision.
        request.HasIndex(r => new { r.ObjectId, r.Role }).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("IX_RoleChangeRequests_Pending");
        request.HasIndex(r => new { r.Status, r.RequestedAt });

        var grant = modelBuilder.Entity<RoleGrant>();
        grant.ToTable("RoleGrants");
        grant.HasKey(g => g.Id);
        grant.Property(g => g.Id).ValueGeneratedNever();
        grant.Property(g => g.ObjectId).HasMaxLength(StaffMember.MaxObjectIdLength).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        grant.Property(g => g.Role).HasMaxLength(50).IsUnicode(false);
        grant.Property<byte[]>("RowVersion").IsRowVersion();
        grant.Ignore(g => g.IsActive);
        // One active grant per account and role.
        grant.HasIndex(g => new { g.ObjectId, g.Role }).IsUnique().HasFilter("[RevokedAt] IS NULL").HasDatabaseName("IX_RoleGrants_Active");

        modelBuilder.AddAuditLog(); // ADR 0022: requests and decisions, saved with them
    }
}

internal sealed class SqlStaffStore(AccessDbContext db) : IStaffStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<StaffMember?> FindByIdentityAsync(string issuer, string objectId, CancellationToken cancellationToken) =>
        db.StaffMembers.AsNoTracking().SingleOrDefaultAsync(m => m.IdentityIssuer == issuer && m.IdentityObjectId == objectId, cancellationToken);

    public async Task<bool> TryAddAsync(StaffMember member, CancellationToken cancellationToken)
    {
        db.StaffMembers.Add(member);
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

internal sealed class SqlRoleGrantStore(AccessDbContext db) : IRoleGrantStore
{
    public Task<RoleChangeRequest?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.Set<RoleChangeRequest>().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<RoleChangeRequest>> FindRequestsAsync(
        RoleChangeStatus status, (DateTimeOffset RequestedAt, Guid Id)? before, int limit, CancellationToken cancellationToken) =>
        await db.Set<RoleChangeRequest>().AsNoTracking()
            .Where(r => r.Status == status)
            .Where(r => before == null || r.RequestedAt < before.Value.RequestedAt || (r.RequestedAt == before.Value.RequestedAt && r.Id.CompareTo(before.Value.Id) < 0))
            .OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<RoleGrant?> FindActiveGrantAsync(string objectId, string role, CancellationToken cancellationToken) =>
        db.Set<RoleGrant>().SingleOrDefaultAsync(g => g.ObjectId == objectId && g.Role == role && g.RevokedAt == null, cancellationToken);

    public async Task<IReadOnlyList<RoleGrant>> FindActiveGrantsAsync(CancellationToken cancellationToken) =>
        await db.Set<RoleGrant>().AsNoTracking().Where(g => g.RevokedAt == null).OrderBy(g => g.ObjectId).ThenBy(g => g.Role).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> FindActiveRolesAsync(string objectId, CancellationToken cancellationToken) =>
        await db.Set<RoleGrant>().AsNoTracking().Where(g => g.ObjectId == objectId && g.RevokedAt == null).Select(g => g.Role).ToListAsync(cancellationToken);

    public void Add(RoleChangeRequest request) => db.Set<RoleChangeRequest>().Add(request);

    public void Add(RoleGrant grant) => db.Set<RoleGrant>().Add(grant);

    public void Audit(AuditEntry entry) => db.Set<AuditEntry>().Add(entry);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules).</summary>
internal sealed class AccessDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccessDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AccessDbContext.Schema))
            .Options);
}
