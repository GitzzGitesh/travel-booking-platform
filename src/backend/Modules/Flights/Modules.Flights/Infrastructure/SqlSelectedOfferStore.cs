using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TravelBooking.Modules.Flights.Application;
using TravelBooking.Modules.Flights.Domain;

namespace TravelBooking.Modules.Flights.Infrastructure;

internal sealed class SqlSelectedOfferStore(FlightsDbContext db) : ISelectedOfferStore
{
    // SQL Server duplicate-key errors: unique index (2601) and unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public Task<SelectedOffer?> FindAsync(Guid searchId, Guid offerId, CancellationToken cancellationToken) =>
        db.SelectedOffers.AsNoTracking().SingleOrDefaultAsync(o => o.SearchId == searchId && o.OfferId == offerId, cancellationToken);

    public Task<SelectedOffer?> FindForUpdateAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
        db.SelectedOffers.SingleOrDefaultAsync(o => o.Id == selectedOfferId, cancellationToken);

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

    public async Task<bool> TryAddAsync(SelectedOffer offer, CancellationToken cancellationToken)
    {
        db.SelectedOffers.Add(offer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            db.Entry(offer).State = EntityState.Detached;
            return false;
        }
    }
}
