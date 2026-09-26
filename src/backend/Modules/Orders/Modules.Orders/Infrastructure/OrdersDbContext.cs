using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Infrastructure;

/// <summary>The Orders module's own schema (ADR 0002, database rules): no other module reads or writes it.</summary>
internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";
    public const string ConnectionStringName = "Orders";

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var order = modelBuilder.Entity<Order>();
        order.ToTable("Orders");
        order.HasKey(o => o.Id);
        order.Property(o => o.Id).ValueGeneratedNever();
        order.Property(o => o.IdempotencyKey).HasMaxLength(Application.CreateFlightOrderHandler.MaxIdempotencyKeyLength).IsUnicode(false);
        order.HasIndex(o => o.IdempotencyKey).IsUnique(); // idempotent creation, enforced by the database
        order.Ignore(o => o.Total);
        order.Ignore(o => o.Status); // derived from the items
        order.Property(o => o.PaymentAuthorizationId).HasMaxLength(100);
        order.Property<byte[]>("RowVersion").IsRowVersion();

        order.HasMany(o => o.Items).WithOne().HasForeignKey("OrderId").IsRequired().OnDelete(DeleteBehavior.Restrict);
        order.Navigation(o => o.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        order.HasMany(o => o.Timeline).WithOne().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
        order.Navigation(o => o.Timeline).HasField("_timeline").UsePropertyAccessMode(PropertyAccessMode.Field);

        var item = modelBuilder.Entity<FlightOrderItem>();
        item.ToTable("FlightOrderItems", table => table.HasCheckConstraint(
            "CK_FlightOrderItems_Status",
            $"[Status] IN ({string.Join(", ", Enum.GetNames<FlightOrderItemStatus>().Select(name => $"'{name}'"))})"));
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).ValueGeneratedNever();
        item.HasIndex(i => i.SelectedOfferId).IsUnique(); // at most one order item books a selection
        item.ComplexProperty(i => i.AgreedPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("AgreedAmount").HasPrecision(19, 4);
            money.Property(m => m.Currency).HasColumnName("AgreedCurrency").HasColumnType("char(3)")
                .HasConversion(code => code.Value, value => new CurrencyCode(value));
        });
        item.Property(i => i.Status).HasConversion<string>().HasMaxLength(30);
        item.Property(i => i.ProviderId).HasMaxLength(50);
        item.Property(i => i.SupplierLocator).HasMaxLength(100);

        var timeline = modelBuilder.Entity<OrderTimelineEntry>();
        timeline.ToTable("OrderTimeline");
        timeline.HasKey(e => e.Id);
        timeline.Property(e => e.Id).UseIdentityColumn();
        timeline.HasIndex(e => new { e.OrderId, e.Id });
        timeline.Property(e => e.Actor).HasMaxLength(100);
        timeline.Property(e => e.FromStatus).HasMaxLength(30);
        timeline.Property(e => e.ToStatus).HasMaxLength(30);
        timeline.Property(e => e.Reason).HasMaxLength(500);
        timeline.Property(e => e.CorrelationId).HasMaxLength(100);
        timeline.Property(e => e.ProviderReference).HasMaxLength(200);
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a host. It never connects (database rules).</summary>
internal sealed class OrdersDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.Schema))
            .Options);
}
