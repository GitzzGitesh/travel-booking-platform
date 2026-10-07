using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Background.Persistence;
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
        modelBuilder.AddOutbox().AddInbox().AddJobLeases(); // ADR 0007: events for other modules, refund outcomes from Payments, and the jobs' leases
        modelBuilder.AddAuditLog(); // ADR 0022: staff actions on orders, saved with the action

        // ADR 0027: one refund case per refund; its id is Payments' refund id (the provider key).
        var refund = modelBuilder.Entity<RefundCase>();
        refund.ToTable("RefundCases", table => table.HasCheckConstraint(
            "CK_RefundCases_Status", $"[Status] IN ({string.Join(", ", Enum.GetNames<RefundCaseStatus>().Select(name => $"'{name}'"))})"));
        refund.HasKey(r => r.Id);
        refund.Property(r => r.Id).ValueGeneratedNever();
        refund.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        refund.Property(r => r.ItemIds).HasMaxLength(1000).IsUnicode(false);
        refund.Property(r => r.AmountValue).HasColumnName("Amount").HasPrecision(19, 4);
        refund.Property(r => r.CurrencyCode).HasColumnName("Currency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        refund.Ignore(r => r.Amount);
        refund.Property(r => r.SupplierRefundValue).HasColumnName("SupplierRefund").HasPrecision(19, 4);
        refund.Property(r => r.FeeValue).HasColumnName("Fee").HasPrecision(19, 4);
        refund.Property(r => r.SupplierReference).HasMaxLength(RefundCase.MaxTextLength);
        refund.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        refund.Property(r => r.Reason).HasMaxLength(RefundCase.MaxTextLength);
        refund.Property(r => r.RequestedBy).HasMaxLength(64).IsUnicode(false);
        refund.Property(r => r.RequestedByAccount).HasMaxLength(128).IsUnicode(false);
        refund.Property(r => r.DecidedBy).HasMaxLength(64).IsUnicode(false);
        refund.Property(r => r.DecisionReason).HasMaxLength(RefundCase.MaxTextLength);
        refund.Property<byte[]>("RowVersion").IsRowVersion();
        refund.Property(r => r.IdempotencyKey).HasMaxLength(RefundCase.MaxKeyLength).IsUnicode(false);
        refund.Property(r => r.RequestFingerprint).HasMaxLength(RefundCase.MaxFingerprintLength).IsUnicode(false);
        refund.Ignore(r => r.CancelledItemIds);
        // Idempotency (non-negotiable 4): one case per requester and key, enforced by the database.
        refund.HasIndex(r => new { r.RequestedBy, r.IdempotencyKey }).IsUnique();
        refund.HasIndex(r => r.OrderId);
        refund.HasIndex(r => new { r.Status, r.RequestedAt });

        // ADR 0029: customers' cancellation requests.
        var cancellation = modelBuilder.Entity<CancellationRequest>();
        cancellation.ToTable("CancellationRequests", table => table.HasCheckConstraint(
            "CK_CancellationRequests_Status", $"[Status] IN ({string.Join(", ", Enum.GetNames<CancellationRequestStatus>().Select(name => $"'{name}'"))})"));
        cancellation.HasKey(r => r.Id);
        cancellation.Property(r => r.Id).ValueGeneratedNever();
        cancellation.Property(r => r.CustomerId).HasMaxLength(Order.MaxCustomerIdLength);
        cancellation.Property(r => r.IdempotencyKey).HasMaxLength(CancellationRequest.MaxKeyLength).IsUnicode(false);
        cancellation.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        cancellation.Property(r => r.ResolvedBy).HasMaxLength(Order.MaxCustomerIdLength + 10);
        cancellation.Property(r => r.ResolutionNote).HasMaxLength(CancellationRequest.MaxReasonLength);
        cancellation.Property<byte[]>("RowVersion").IsRowVersion();
        // Idempotency (non-negotiable 4): one request per customer and key; and at most one open request per order.
        cancellation.HasIndex(r => new { r.CustomerId, r.IdempotencyKey }).IsUnique();
        cancellation.HasIndex(r => r.OrderId).IsUnique().HasFilter("[Status] = 'Open'").HasDatabaseName("IX_CancellationRequests_OrderId_Open");
        cancellation.HasIndex(r => new { r.Status, r.RequestedAt });

        var order = modelBuilder.Entity<Order>();
        order.ToTable("Orders");
        order.HasKey(o => o.Id);
        order.Property(o => o.Id).ValueGeneratedNever();
        order.Property(o => o.IdempotencyKey).HasMaxLength(Application.CreateFlightOrderHandler.MaxIdempotencyKeyLength).IsUnicode(false);
        order.Property(o => o.CustomerId).HasMaxLength(Order.MaxCustomerIdLength);

        // Idempotent creation per customer, enforced by the database; also the customer's order-history lookup path.
        order.HasIndex(o => new { o.CustomerId, o.IdempotencyKey }).IsUnique();
        order.HasIndex(o => new { o.CustomerId, o.CreatedAt, o.Id }); // the customer's trips, newest first (ADR 0029)
        order.Ignore(o => o.Total);
        order.Ignore(o => o.Status); // derived from the items
        order.Property(o => o.PaymentAuthorizationId).HasMaxLength(100);
        order.Property<byte[]>("RowVersion").IsRowVersion();

        order.HasMany(o => o.Items).WithOne().HasForeignKey("OrderId").IsRequired().OnDelete(DeleteBehavior.Restrict);
        order.Navigation(o => o.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        order.HasMany(o => o.Timeline).WithOne().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
        order.Navigation(o => o.Timeline).HasField("_timeline").UsePropertyAccessMode(PropertyAccessMode.Field);

        var item = modelBuilder.Entity<FlightOrderItem>();
        // The table keeps its name: items of every product live in it (ADR 0030 §6), told apart by Product.
        item.ToTable("FlightOrderItems", table =>
        {
            table.HasCheckConstraint(
                "CK_FlightOrderItems_Status",
                $"[Status] IN ({string.Join(", ", Enum.GetNames<FlightOrderItemStatus>().Select(name => $"'{name}'"))})");
            table.HasCheckConstraint(
                "CK_FlightOrderItems_Product",
                $"[Product] IN ({string.Join(", ", Enum.GetNames<OrderProduct>().Select(name => $"'{name}'"))})");
        });
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).ValueGeneratedNever();
        item.HasIndex(i => i.SelectedOfferId).IsUnique(); // at most one order item books a selection
        item.HasIndex(i => new { i.Status, i.OfferExpiresAt }); // the offer-expiry job's work list
        item.ComplexProperty(i => i.AgreedPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("AgreedAmount").HasPrecision(19, 4);
            money.Property(m => m.Currency).HasColumnName("AgreedCurrency").HasColumnType("char(3)")
                .HasConversion(code => code.Value, value => new CurrencyCode(value));
        });
        item.Property(i => i.Status).HasConversion<string>().HasMaxLength(30);
        item.Property(i => i.Product).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        item.Property(i => i.ProviderId).HasMaxLength(50);
        item.Property(i => i.SupplierLocator).HasMaxLength(100);
        item.Property(i => i.Ticketing).HasConversion<string>().HasMaxLength(10);
        item.HasIndex(i => new { i.Status, i.NextBookingLookupAt }); // the booking reconciliation's work list

        // What the item needs from its travellers (Q9): optional, since items created before it have none.
        item.OwnsOne(i => i.TravellerNeeds, needs =>
        {
            needs.Property(n => n.Adults).HasColumnName("TravellerAdults");
            needs.Property(n => n.Children).HasColumnName("TravellerChildren");
            needs.Property(n => n.Infants).HasColumnName("TravellerInfants");
            needs.Property(n => n.DocumentsRequired).HasColumnName("DocumentsRequired");
            needs.Property(n => n.LastTravelDate).HasColumnName("LastTravelDate");
            needs.Ignore(n => n.IsKnown);
        });

        // A hotel rate's agreed cancellation terms (ADR 0030 §7): optional, since flights have none.
        item.OwnsOne(i => i.CancellationTerms, terms =>
        {
            terms.Property(t => t.Refundable).HasColumnName("CancellationRefundable");
            terms.Property(t => t.FreeUntil).HasColumnName("FreeCancellationUntil");
            terms.Property(t => t.PenaltyAmount).HasColumnName("CancellationPenaltyAmount").HasPrecision(19, 4);
        });

        var timeline = modelBuilder.Entity<OrderTimelineEntry>();
        timeline.ToTable("OrderTimeline");
        timeline.HasKey(e => e.Id);
        timeline.Property(e => e.Id).UseIdentityColumn();
        timeline.HasIndex(e => new { e.OrderId, e.Id });
        timeline.Property(e => e.Actor).HasMaxLength(150); // "customer:" + a customer id of up to 128
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
