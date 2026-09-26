using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>What a supplier booking call established, in the core's terms (ADR 0005, booking-lifecycle.md).</summary>
internal abstract record SupplierBookingOutcome
{
    private SupplierBookingOutcome()
    {
    }

    /// <summary>The booking exists at the supplier under our reference, at the agreed price.</summary>
    internal sealed record Booked(FlightBookingConfirmation Confirmation) : SupplierBookingOutcome;

    /// <summary>
    /// The supplier definitely did not book (a definitive refusal): safe to fail the item and release the payment
    /// authorization.
    /// </summary>
    internal sealed record NotBooked(SupplierBookingFailureReason Reason) : SupplierBookingOutcome;

    /// <summary>
    /// The outcome is unknown (timeout, ambiguous or possibly-processed refusal, exception, or a failed lookup): the
    /// item stays PendingConfirmation and is reconciled by our reference. The booking call is NEVER resubmitted, and
    /// payment is never voided from here. <paramref name="Cause"/> is for diagnostics only.
    /// </summary>
    internal sealed record Unknown(ProviderErrorKind Cause) : SupplierBookingOutcome;

    /// <summary>
    /// A lookup by our reference found no booking AS OF <paramref name="At"/>. Not conclusive by itself: a supplier's
    /// lookup can lag its writes. Reconciliation treats it as "not booked" only once that supplier's consistency
    /// window (recorded in its ADR) has passed since the booking attempt.
    /// </summary>
    internal sealed record NotFound(DateTimeOffset At) : SupplierBookingOutcome;

    /// <summary>
    /// A booking exists but not as agreed (another price, reference or provider): never treated as Booked or captured;
    /// it goes to ManualReview (booking-lifecycle.md).
    /// </summary>
    internal sealed record Mismatch(FlightBookingConfirmation Confirmation) : SupplierBookingOutcome;
}

internal enum SupplierBookingFailureReason
{
    Rejected,
    PriceChanged,
    SoldOut,
    OfferExpired,
    InvalidRequest,
}

/// <summary>
/// Makes supplier booking calls for the booking orchestration (Phase 3 Order items call this; nothing is persisted
/// here, because a supplier booking belongs to an order item, ADR 0005). Exactly one supplier write per call: no retry.
/// </summary>
internal sealed partial class FlightSupplierBooking(FlightProviders providers, TimeProvider timeProvider, ILogger<FlightSupplierBooking> logger)
{
    public async Task<SupplierBookingOutcome> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken)
    {
        // The offer's own provider books it; one that is not composed here is refused before anything is sent.
        if (providers.Find(details.Offer.ProviderId) is not { } provider)
        {
            return new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.InvalidRequest);
        }

        // Cancelled before anything is sent: nothing was attempted, so the caller's cancellation simply propagates.
        cancellationToken.ThrowIfCancellationRequested();

        Result<FlightBookingConfirmation, ProviderError> result;
        try
        {
            result = await provider.BookAsync(details, cancellationToken);
        }
        catch (Exception exception)
        {
            // Once the write has started, any exception (a timeout, a reset connection, or cancellation mid-call) may
            // hide a booking that was made: unknown, never a failure.
            LogBookingException(logger, provider.Id, details.ClientReference.Value, exception.GetType().Name);
            return new SupplierBookingOutcome.Unknown(ProviderErrorKind.Unknown);
        }

        if (result.IsSuccess)
        {
            return Verify(provider, result.Value, details.ClientReference, details.ExpectedTotalPrice);
        }

        var outcome = Classify(result.Error.Kind);
        if (outcome is SupplierBookingOutcome.Unknown)
        {
            LogUnknownBooking(logger, provider.Id, details.ClientReference.Value, result.Error.Kind);
        }

        return outcome;
    }

    /// <summary>
    /// Looks the booking up by our reference after an unknown outcome. A read: safe to repeat. A found booking is
    /// checked against what was agreed, exactly as a booking response is.
    /// </summary>
    public async Task<SupplierBookingOutcome> ReconcileAsync(string providerId, ClientReference clientReference, Money expectedTotalPrice, CancellationToken cancellationToken)
    {
        // The provider the booking was sent to. Not composed here: nothing can be looked up yet, so the outcome stays unknown.
        if (providers.Find(providerId) is not { } provider)
        {
            return new SupplierBookingOutcome.Unknown(ProviderErrorKind.Unavailable);
        }

        var lookup = await provider.RetrieveBookingAsync(clientReference, cancellationToken);
        if (!lookup.IsSuccess)
        {
            return new SupplierBookingOutcome.Unknown(lookup.Error.Kind);
        }

        return lookup.Value.Booking is { } found
            ? Verify(provider, found, clientReference, expectedTotalPrice)
            : new SupplierBookingOutcome.NotFound(timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Only refusals that prove nothing was booked are definitive. Unavailable, RateLimited and AuthFailure on a WRITE
    /// stay unknown: a shared status mapper cannot prove the request was not processed, and one lookup settles it.
    /// </summary>
    internal static SupplierBookingOutcome Classify(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Rejected => new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.Rejected),
        ProviderErrorKind.PriceChanged => new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.PriceChanged),
        ProviderErrorKind.SoldOut => new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.SoldOut),
        ProviderErrorKind.OfferExpired => new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.OfferExpired),
        ProviderErrorKind.InvalidRequest => new SupplierBookingOutcome.NotBooked(SupplierBookingFailureReason.InvalidRequest),
        _ => new SupplierBookingOutcome.Unknown(kind),
    };

    private SupplierBookingOutcome Verify(IFlightProvider provider, FlightBookingConfirmation confirmation, ClientReference clientReference, Money expectedTotalPrice)
    {
        if (confirmation.ClientReference == clientReference
            && confirmation.Booking.ProviderId == provider.Id
            && confirmation.TotalPrice == expectedTotalPrice)
        {
            return new SupplierBookingOutcome.Booked(confirmation);
        }

        LogMismatch(logger, provider.Id, clientReference.Value);
        return new SupplierBookingOutcome.Mismatch(confirmation);
    }

    // Our reference and the provider only: never passenger data or supplier messages (security rules).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight booking outcome unknown at provider {ProviderId} for client reference {ClientReference} ({ErrorKind}); it will be reconciled, never resubmitted.")]
    private static partial void LogUnknownBooking(ILogger logger, string providerId, string clientReference, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight booking call to provider {ProviderId} for client reference {ClientReference} threw {ExceptionType}; outcome unknown, it will be reconciled, never resubmitted.")]
    private static partial void LogBookingException(ILogger logger, string providerId, string clientReference, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight booking at provider {ProviderId} for client reference {ClientReference} does not match what was agreed; manual review required.")]
    private static partial void LogMismatch(ILogger logger, string providerId, string clientReference);
}
