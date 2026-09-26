using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.Infrastructure;

/// <summary>The Payments module's own schema (ADR 0002, database rules): no other module reads or writes it.</summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "payments";
    public const string ConnectionStringName = "Payments";

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var attempt = modelBuilder.Entity<PaymentAttempt>();
        attempt.ToTable("PaymentAttempts", table => table.HasCheckConstraint(
            "CK_PaymentAttempts_Status",
            $"[Status] IN ({string.Join(", ", Enum.GetNames<PaymentAttemptStatus>().Select(name => $"'{name}'"))})"));
        attempt.HasKey(a => a.Id);
        attempt.Property(a => a.Id).ValueGeneratedNever();

        // One attempt per order and key (idempotency, enforced by the database). No foreign key to Orders (ADR 0002).
        attempt.HasIndex(a => new { a.OrderId, a.IdempotencyKey }).IsUnique();

        // At most one attempt per order that holds, or may hold, the customer's funds (F-32: two tabs, two keys): a new
        // attempt starts only once the previous one is known to hold nothing.
        attempt.HasIndex(a => a.OrderId).IsUnique().HasDatabaseName("IX_PaymentAttempts_OrderId_Live")
            .HasFilter($"[Status] IN ({string.Join(", ", PaymentAttempt.LiveStatuses.Select(status => $"'{status}'"))})");
        attempt.Property(a => a.IdempotencyKey).HasMaxLength(AuthorizeOrderPaymentHandler.MaxIdempotencyKeyLength).IsUnicode(false);
        attempt.Property(a => a.CustomerId).HasMaxLength(PaymentAttempt.MaxCustomerIdLength);
        attempt.ComplexProperty(a => a.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("Amount").HasPrecision(19, 4);
            money.Property(m => m.Currency).HasColumnName("Currency").HasColumnType("char(3)")
                .HasConversion(code => code.Value, value => new CurrencyCode(value));
        });
        attempt.Property(a => a.Status).HasConversion<string>().HasMaxLength(30);
        attempt.Property(a => a.DeclineReason).HasMaxLength(30);
        attempt.Property(a => a.ProviderId).HasMaxLength(50);
        attempt.Property(a => a.ProviderPaymentId).HasMaxLength(255);
        attempt.Ignore(a => a.Reference);
        attempt.Ignore(a => a.IsFinal);
        attempt.Property<byte[]>("RowVersion").IsRowVersion();

        attempt.HasMany(a => a.Events).WithOne().HasForeignKey(e => e.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
        attempt.Navigation(a => a.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);

        var events = modelBuilder.Entity<PaymentAttemptEvent>();
        events.ToTable("PaymentAttemptEvents");
        events.HasKey(e => e.Id);
        events.Property(e => e.Id).UseIdentityColumn();
        events.HasIndex(e => new { e.PaymentAttemptId, e.Id });
        events.Property(e => e.Actor).HasMaxLength(100);
        events.Property(e => e.CorrelationId).HasMaxLength(100);
        events.Property(e => e.FromStatus).HasMaxLength(30);
        events.Property(e => e.ToStatus).HasMaxLength(30);
        events.Property(e => e.Reason).HasMaxLength(500);
        events.Property(e => e.ProviderReference).HasMaxLength(255);
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules).</summary>
internal sealed class PaymentsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema))
            .Options);
}
