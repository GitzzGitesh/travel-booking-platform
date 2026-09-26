using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Infrastructure;

/// <summary>The Flights module's own schema (ADR 0002, database rules): no other module reads or writes it.</summary>
internal sealed class FlightsDbContext(DbContextOptions<FlightsDbContext> options) : DbContext(options)
{
    public const string Schema = "flights";
    public const string ConnectionStringName = "Flights";

    public DbSet<SelectedOffer> SelectedOffers => Set<SelectedOffer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var offer = modelBuilder.Entity<SelectedOffer>();
        offer.ToTable("SelectedOffers", table =>
        {
            table.HasCheckConstraint(
                "CK_SelectedOffers_Status",
                $"[Status] IN ({string.Join(", ", Enum.GetNames<SelectedOfferStatus>().Select(name => $"'{name}'"))})");

            // An optional price is either complete (amount and currency) or absent.
            foreach (var prefix in new[] { "Confirmed", "Quoted" })
            {
                table.HasCheckConstraint(
                    $"CK_SelectedOffers_{prefix}Price",
                    $"([{prefix}Amount] IS NULL AND [{prefix}Currency] IS NULL) OR ([{prefix}Amount] IS NOT NULL AND [{prefix}Currency] IS NOT NULL)");
            }
        });
        offer.HasKey(o => o.Id);
        offer.Property(o => o.Id).ValueGeneratedNever();

        // Idempotent selection: one snapshot per offer of a search, enforced by the database (booking rules).
        offer.HasIndex(o => new { o.SearchId, o.OfferId }).IsUnique();

        offer.Property(o => o.ProviderId).HasMaxLength(50);
        offer.Property(o => o.ProviderOfferToken); // Opaque adapter token, stored verbatim; may be long (ADR 0014).
        offer.ComplexProperty(o => o.TotalPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("TotalAmount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                .HasColumnName("Currency")
                .HasColumnType("char(3)")
                .HasConversion(code => code.Value, value => new CurrencyCode(value));
        });
        offer.Property(o => o.Cabin).HasConversion<string>().HasMaxLength(20);
        offer.Property(o => o.Slices)
            .HasColumnName("SlicesJson")
            .HasConversion(
                slices => SliceSnapshotJson.Write(slices),
                json => SliceSnapshotJson.Read(json),
                new ValueComparer<IReadOnlyList<FlightSlice>>(
                    (left, right) => SliceSnapshotJson.Write(left!) == SliceSnapshotJson.Write(right!),
                    slices => SliceSnapshotJson.Write(slices).GetHashCode(StringComparison.Ordinal),
                    slices => slices));

        // Fare facts (price breakdown, validating carrier, baggage, conditions, ticketing deadline), versioned JSON.
        // Nullable: rows selected before they were kept have none.
        offer.Ignore(o => o.Fare);
        offer.Property<FlightFare?>("StoredFare")
            .HasColumnName("FareJson")
            .HasConversion(
                fare => FareSnapshotJson.Write(fare!),
                json => FareSnapshotJson.Read(json),
                new ValueComparer<FlightFare?>(
                    (left, right) => (left == null && right == null) || (left != null && right != null && FareSnapshotJson.Write(left) == FareSnapshotJson.Write(right)),
                    fare => fare == null ? 0 : FareSnapshotJson.Write(fare).GetHashCode(StringComparison.Ordinal),
                    fare => fare));

        // Revalidation state (F-01..F-03). The migration gives existing rows the Selected status.
        offer.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        offer.Ignore(o => o.ConfirmedPrice);
        offer.Ignore(o => o.QuotedPrice);
        MapOptionalMoney(offer, "Confirmed");
        MapOptionalMoney(offer, "Quoted");

        // Optimistic concurrency: a revalidation and an acceptance racing on the same row cannot both win.
        offer.Property<byte[]>("RowVersion").IsRowVersion();
    }

    private static void MapOptionalMoney(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<SelectedOffer> offer, string prefix)
    {
        offer.Property<decimal?>($"{prefix}Amount").HasPrecision(19, 4);
        offer.Property<CurrencyCode?>($"{prefix}Currency")
            .HasColumnType("char(3)")
            .HasConversion(code => code!.Value.Value, value => new CurrencyCode(value));
    }
}

/// <summary>
/// Lets `dotnet ef migrations add` build the model without a running host. It never connects: migrations are applied
/// by tests and, in deployed environments, by the pipeline (database rules), never at application startup.
/// </summary>
internal sealed class FlightsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FlightsDbContext>
{
    public FlightsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FlightsDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", FlightsDbContext.Schema))
            .Options);
}
