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
        offer.ToTable("SelectedOffers");
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

        offer.Property<byte[]>("RowVersion").IsRowVersion();
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
