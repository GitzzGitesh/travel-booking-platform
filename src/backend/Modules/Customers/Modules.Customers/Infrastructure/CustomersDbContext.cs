using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
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
    }
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
