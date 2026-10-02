using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Notifications.Application;
using TravelBooking.Modules.Notifications.Domain;

namespace TravelBooking.Modules.Notifications.Infrastructure;

/// <summary>The Notifications module's own schema (ADR 0002, ADR 0024): notices and their delivery state, no personal data.</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";
    public const string ConnectionStringName = "Notifications";

    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var notification = modelBuilder.Entity<Notification>();
        notification.ToTable("Notifications", table => table.HasCheckConstraint(
            "CK_Notifications_Status", "[Status] IN ('Pending','Sending','Accepted','Delivered','Bounced','Suppressed','Failed')"));
        notification.HasKey(n => n.Id);
        notification.Property(n => n.Id).ValueGeneratedNever();
        notification.Property(n => n.Kind).HasMaxLength(Notification.MaxKindLength).IsUnicode(false);
        notification.Property(n => n.Values).HasMaxLength(Notification.MaxValuesLength);
        notification.Property(n => n.Culture).HasMaxLength(20).IsUnicode(false);
        notification.Property(n => n.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        notification.Property(n => n.ProviderMessageId).HasMaxLength(Notification.MaxProviderMessageIdLength).IsUnicode(false);
        notification.Property(n => n.LastError).HasMaxLength(Notification.MaxErrorLength).IsUnicode(false);
        notification.Property<byte[]>("RowVersion").IsRowVersion();

        // One notice per business event and kind: a redelivered event never sends a second one.
        notification.HasIndex(n => new { n.SourceEventId, n.Kind }).IsUnique();
        // The send job's work list.
        notification.HasIndex(n => new { n.Status, n.NextAttemptAt });
        notification.HasIndex(n => n.OrderId);

        modelBuilder.AddJobLeases(); // ADR 0007: the send job's lease
    }
}

internal sealed class SqlNotificationStore(NotificationsDbContext db) : INotificationStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public async Task<bool> TryAddAsync(Notification notification, CancellationToken cancellationToken)
    {
        db.Notifications.Add(notification);
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

    public async Task<IReadOnlyList<Guid>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => (n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Sending) && n.NextAttemptAt <= now)
            .OrderBy(n => n.NextAttemptAt)
            .Select(n => n.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Notifications.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>For migrations tooling only (dotnet ef): no real connection is opened at design time.</summary>
internal sealed class NotificationsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema))
            .Options);
}
