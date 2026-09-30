using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Infrastructure;

/// <summary>The Payments module's own schema (ADR 0002, database rules): no other module reads or writes it.</summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "payments";
    public const string ConnectionStringName = "Payments";

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<PaymentNotificationRecord> PaymentNotifications => Set<PaymentNotificationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddInbox().AddJobLeases(); // ADR 0007: consumed events, and the reconciliation job's lease

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
        attempt.Property(a => a.ReleaseReason).HasMaxLength(PaymentAttempt.MaxReleaseReasonLength);
        attempt.Property(a => a.CaptureAmountValue).HasColumnName("CaptureAmount").HasPrecision(19, 4);
        attempt.Ignore(a => a.CaptureAmount);
        attempt.Ignore(a => a.CaptureKey);
        attempt.Ignore(a => a.IsCaptureInProgress);

        // The reconciliation job's work list.
        attempt.HasIndex(a => new { a.Status, a.UpdatedAt });

        // The per-customer attempt limit (customer id + created date: database rules).
        attempt.HasIndex(a => new { a.CustomerId, a.CreatedAt });

        // Finding an attempt from a provider notification that carries only the provider's payment id: one attempt per
        // provider payment, enforced by the database.
        attempt.HasIndex(a => new { a.ProviderId, a.ProviderPaymentId }).IsUnique().HasFilter("[ProviderPaymentId] IS NOT NULL");
        attempt.Ignore(a => a.Reference);
        attempt.Ignore(a => a.VoidKey);
        attempt.Ignore(a => a.IsAuthorizationSettled);
        attempt.Ignore(a => a.IsVoidInProgress);
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

        // Refusals by the payment attempt limits (Q10): append-only, for the review list and alerting.
        var trip = modelBuilder.Entity<AttemptLimitTrip>();
        trip.ToTable("AttemptLimitTrips");
        trip.HasKey(t => t.Id);
        trip.Property(t => t.Id).UseIdentityColumn();
        trip.Property(t => t.CustomerId).HasMaxLength(PaymentAttempt.MaxCustomerIdLength);
        trip.Property(t => t.IdempotencyKey).HasMaxLength(AuthorizeOrderPaymentHandler.MaxIdempotencyKeyLength).IsUnicode(false);
        trip.HasIndex(t => new { t.CustomerId, t.IdempotencyKey }).IsUnique(); // a retried refusal is one trip
        trip.HasIndex(t => new { t.CustomerId, t.At });
        trip.HasIndex(t => t.At);

        // Provider notifications (webhooks): one row per provider event (deduplication by a unique constraint).
        var notification = modelBuilder.Entity<PaymentNotificationRecord>();
        notification.ToTable("PaymentNotifications");
        notification.HasKey(n => n.Id);
        notification.Property(n => n.Id).ValueGeneratedNever();
        notification.HasIndex(n => new { n.ProviderId, n.EventId }).IsUnique();
        notification.Property(n => n.ProviderId).HasMaxLength(50);
        notification.Property(n => n.EventId).HasMaxLength(PaymentNotification.MaxEventIdLength).IsUnicode(false);
        notification.Property(n => n.Kind).HasConversion<string>().HasMaxLength(20);
        notification.Property(n => n.Reference).HasMaxLength(PaymentReference.MaxLength).IsUnicode(false);
        notification.Property(n => n.ProviderPaymentId).HasMaxLength(255);
        notification.Property(n => n.Outcome).HasMaxLength(PaymentNotificationRecord.MaxOutcomeLength);
        notification.Property(n => n.EventType).HasMaxLength(PaymentNotification.MaxEventTypeLength).IsUnicode(false);

        // The processing job's work list.
        notification.HasIndex(n => new { n.ProcessedAt, n.ReceivedAt });
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
