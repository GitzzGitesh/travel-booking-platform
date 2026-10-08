using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Hotels.Application;
using TravelBooking.Modules.Hotels.Domain;

namespace TravelBooking.Modules.Hotels.Infrastructure;

/// <summary>The Hotels module's own schema (ADR 0002, ADR 0030): no other module reads or writes it.</summary>
internal sealed class HotelsDbContext(DbContextOptions<HotelsDbContext> options) : DbContext(options)
{
    public const string Schema = "hotels";
    public const string ConnectionStringName = "Hotels";

    public DbSet<HotelSelection> Selections => Set<HotelSelection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var selection = modelBuilder.Entity<HotelSelection>();
        selection.ToTable("Selections", table =>
        {
            table.HasCheckConstraint("CK_Selections_Status",
                $"[Status] IN ({string.Join(", ", Enum.GetNames<HotelSelectionStatus>().Select(name => $"'{name}'"))})");
            // An optional amount is either complete (amount and currency) or absent.
            foreach (var prefix in new[] { "Fees", "Penalty", "Confirmed", "Quoted" })
            {
                table.HasCheckConstraint($"CK_Selections_{prefix}Price",
                    $"([{prefix}Amount] IS NULL AND [{prefix}Currency] IS NULL) OR ([{prefix}Amount] IS NOT NULL AND [{prefix}Currency] IS NOT NULL)");
            }

            table.HasCheckConstraint("CK_Selections_Stay", "[CheckOut] > [CheckIn]");
        });
        selection.HasKey(s => s.Id);
        selection.Property(s => s.Id).ValueGeneratedNever();
        // Idempotent selection: one snapshot per offer of a search and caller, enforced by the database (booking rules).
        selection.HasIndex(s => new { s.SearchId, s.OfferId, s.CustomerId }).IsUnique().HasFilter(null);
        selection.Property(s => s.CustomerId).HasMaxLength(HotelSelection.MaxCustomerIdLength).IsUnicode(false);
        selection.Property(s => s.ProviderId).HasMaxLength(50);
        selection.Property(s => s.ProviderOfferToken); // opaque adapter token, stored verbatim
        selection.Property(s => s.Destination).HasColumnType("char(3)");
        selection.Property(s => s.ChildAges).HasMaxLength(20).IsUnicode(false);
        selection.Property(s => s.PropertyId).HasMaxLength(HotelSelection.MaxTextLength);
        selection.Property(s => s.PropertyName).HasMaxLength(HotelSelection.MaxTextLength);
        selection.Property(s => s.AddressLine).HasMaxLength(HotelSelection.MaxTextLength);
        selection.Property(s => s.CityCode).HasColumnType("char(3)");
        selection.Property(s => s.CountryCode).HasColumnType("char(2)");
        selection.Property(s => s.StarRating).HasPrecision(2, 1);
        selection.Property(s => s.TimeZone).HasMaxLength(64).IsUnicode(false);
        selection.Property(s => s.RoomDescription).HasMaxLength(HotelSelection.MaxTextLength);
        selection.Property(s => s.Board).HasConversion<string>().HasMaxLength(20);
        selection.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        selection.ComplexProperty(s => s.TotalPrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("TotalAmount").HasPrecision(19, 4);
            money.Property(m => m.Currency).HasColumnName("Currency").HasColumnType("char(3)")
                .HasConversion(code => code.Value, value => new CurrencyCode(value));
        });
        selection.Ignore(s => s.FeesAtProperty);
        selection.Ignore(s => s.PenaltyAfterDeadline);
        selection.Ignore(s => s.ConfirmedPrice);
        selection.Ignore(s => s.QuotedPrice);
        selection.Ignore(s => s.AgreedPrice);
        selection.Ignore(s => s.Cancellation);
        selection.Ignore(s => s.Nights);
        selection.Ignore(s => s.IsAvailable);
        selection.Ignore(s => s.ChildAgeList);
        foreach (var prefix in new[] { "Fees", "Penalty", "Confirmed", "Quoted" })
        {
            MapOptionalMoney(selection, prefix);
        }

        // Optimistic concurrency: a revalidation and an acceptance racing on the same row cannot both win.
        selection.Property<byte[]>("RowVersion").IsRowVersion();
    }

    private static void MapOptionalMoney(EntityTypeBuilder<HotelSelection> selection, string prefix)
    {
        selection.Property<decimal?>($"{prefix}Amount").HasPrecision(19, 4);
        selection.Property<CurrencyCode?>($"{prefix}Currency").HasColumnType("char(3)")
            .HasConversion(code => code!.Value.Value, value => new CurrencyCode(value));
    }
}

internal sealed class SqlHotelSelectionStore(HotelsDbContext db) : IHotelSelectionStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<HotelSelection?> FindAsync(Guid searchId, Guid offerId, string? customerId, CancellationToken cancellationToken) =>
        db.Selections.AsNoTracking().SingleOrDefaultAsync(s => s.SearchId == searchId && s.OfferId == offerId && s.CustomerId == customerId, cancellationToken);

    public Task<HotelSelection?> FindForUpdateAsync(Guid selectionId, CancellationToken cancellationToken) =>
        db.Selections.SingleOrDefaultAsync(s => s.Id == selectionId, cancellationToken);

    public Task<HotelSelection?> FindByIdAsync(Guid selectionId, CancellationToken cancellationToken) =>
        db.Selections.AsNoTracking().SingleOrDefaultAsync(s => s.Id == selectionId, cancellationToken);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<bool> TryAddAsync(HotelSelection selection, CancellationToken cancellationToken)
    {
        db.Selections.Add(selection);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            db.Entry(selection).State = EntityState.Detached;
            return false;
        }
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a running host; it never connects (database rules).</summary>
internal sealed class HotelsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HotelsDbContext>
{
    public HotelsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HotelsDbContext>()
            .UseSqlServer("Server=design-time-only", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", HotelsDbContext.Schema))
            .Options);
}
