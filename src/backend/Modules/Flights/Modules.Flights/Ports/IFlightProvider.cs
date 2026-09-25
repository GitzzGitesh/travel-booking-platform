using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;

namespace TravelBooking.Modules.Flights.Ports;

/// <summary>
/// The flight supplier port (ADR 0004). Implemented by Integrations.Flights.* adapters, which map supplier models to
/// these types (ADR 0014). Operations are added story by story: revalidate, book, retrieve, and cancel come later.
/// The port is not frozen until it has been validated against two real supplier shapes (Q6).
/// </summary>
public interface IFlightProvider
{
    /// <summary>Stable provider identifier, recorded on every offer reference.</summary>
    string Id { get; }

    /// <summary>
    /// Searches for offers. An idempotent read, so it may be retried on transient failures. No offers is a valid
    /// result (no availability). Supplier failures are returned as a <see cref="ProviderError"/>, never thrown.
    /// </summary>
    Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>A search result. A wrapper, so that search-level data (partial results, paging) can be added without a port break.</summary>
public sealed record FlightSearchResult(IReadOnlyList<FlightOffer> Offers);
