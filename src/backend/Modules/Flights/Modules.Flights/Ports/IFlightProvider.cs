using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;

namespace TravelBooking.Modules.Flights.Ports;

/// <summary>
/// The flight supplier port (ADR 0004). Implemented by Integrations.Flights.* adapters, which map supplier models to
/// these types (ADR 0014). Operations are added story by story: cancellation comes later.
/// The port is not frozen until it has been validated against two real supplier shapes (Q6).
/// </summary>
public interface IFlightProvider
{
    /// <summary>Stable provider identifier, recorded on every offer reference.</summary>
    string Id { get; }

    /// <summary>
    /// What this provider supports, how far its adapter has got, and which operations it implements (Q6). The core
    /// never calls an operation that is not implemented, and never runs an adapter below ProductionReady in Production.
    /// </summary>
    FlightProviderCapabilities Capabilities => FlightProviderCapabilities.NotDeclared;

    /// <summary>
    /// Searches for offers. An idempotent read, so it may be retried on transient failures. No offers is a valid
    /// result (no availability). Supplier failures are returned as a <see cref="ProviderError"/>, never thrown.
    /// </summary>
    Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Revalidates a previously returned offer by its opaque reference: the offer as the supplier prices it NOW, with
    /// its current expiry. An idempotent read, so it may be retried on transient failures. The price may differ from
    /// the one searched; the core decides what a difference means and never passes it on silently (booking rules).
    /// No longer bookable is <see cref="ProviderErrorKind.OfferExpired"/> or <see cref="ProviderErrorKind.SoldOut"/>;
    /// a reference the provider does not recognise is <see cref="ProviderErrorKind.InvalidRequest"/>.
    /// The returned <see cref="FlightOffer.Reference"/> replaces the one revalidated for every later operation (a
    /// supplier may issue a new priced offer); the itinerary is expected to be unchanged.
    /// </summary>
    Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken);

    /// <summary>
    /// Books the offer under OUR <see cref="FlightBookingDetails.ClientReference"/>. A WRITE: never retried blindly
    /// (booking rules). Adapters MUST guarantee at most one booking per client reference: by the supplier's own
    /// idempotency where it has it, otherwise by looking the reference up before booking. A repeat returns the existing
    /// booking (whatever the new details) or an error, never a second booking.
    /// Failures are RETURNED, never thrown; the core treats any exception from a started write as an unknown outcome.
    /// <see cref="ProviderErrorKind.Unknown"/> is a timeout or ambiguous response (the booking MAY exist: reconcile with
    /// <see cref="RetrieveBookingAsync"/>, never resubmit). Unavailable, RateLimited and AuthFailure are also treated
    /// as unknown on this write. Only <see cref="ProviderErrorKind.Rejected"/>,
    /// <see cref="ProviderErrorKind.PriceChanged"/>, <see cref="ProviderErrorKind.SoldOut"/>,
    /// <see cref="ProviderErrorKind.OfferExpired"/> and <see cref="ProviderErrorKind.InvalidRequest"/> mean the
    /// supplier definitely did not book.
    /// </summary>
    Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a booking up by OUR reference: mandatory for reconciliation (provider-integration.md). An idempotent read.
    /// "Not found" is a success without a booking; an error means the answer is still unknown. "Not found" can lag a
    /// supplier's writes: each adapter's ADR records that consistency window, and reconciliation waits it out.
    /// </summary>
    Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken);
}

/// <summary>A search result. A wrapper, so that search-level data (partial results, paging) can be added without a port break.</summary>
public sealed record FlightSearchResult(IReadOnlyList<FlightOffer> Offers);
