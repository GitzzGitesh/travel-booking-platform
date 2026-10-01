using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
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

/// <summary>Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules).</summary>
internal sealed class AccessDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccessDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AccessDbContext.Schema))
            .Options);
}
