using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;

namespace TravelBooking.Modules.Payments.Ports;

/// <summary>
/// The payment provider port (ADR 0004): provider-neutral, so the real provider can be chosen later (ADR 0006 is still
/// Proposed; Q2 and Q5 are open). Implemented by Integrations.Payments.* adapters, which convert amounts to provider
/// minor units (ADR 0010) and map provider errors to the shared <see cref="ProviderErrorKind"/> taxonomy.
/// <para>
/// Every write is idempotent at the provider by OUR key: the authorization by <see cref="PaymentReference"/>, later
/// writes by <see cref="OperationKey"/>. A repeat with the same key returns the original result, never a second effect;
/// the same key with different details is <see cref="ProviderErrorKind.IdempotencyConflict"/> (definitive), and a key
/// still being processed is <see cref="ProviderErrorKind.OperationInProgress"/> (unknown). Failures are RETURNED, never
/// thrown; <see cref="ProviderErrorKind.Unknown"/> means the write may have happened. A decline is a payment state, not
/// an error. <see cref="ProviderError.Message"/> is written by the adapter and never copies a provider message.
/// </para>
/// </summary>
public interface IPaymentProvider
{
    string Id { get; }

    /// <summary>
    /// Holds the amount (manual capture). The payment comes back Authorized, RequiresAction (a customer challenge), or
    /// Declined. A declined reference is final: another attempt uses a new reference.
    /// </summary>
    Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken);

    /// <summary>Takes up to the authorized amount, once per payment. Safe to repeat with the same key.</summary>
    Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken);

    /// <summary>Releases an uncaptured hold. Safe to repeat with the same key.</summary>
    Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// Returns part or all of the captured amount as one refund, identified by our key. Safe to repeat with the same key
    /// within the provider's idempotency window: never a second refund.
    /// </summary>
    Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a payment up by OUR reference, for reconciliation after an unknown outcome. An idempotent read. "Not found"
    /// is a success without a payment; an error means the answer is still unknown.
    /// </summary>
    Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken);

    /// <summary>Looks one refund up by OUR key (the adapter stores it with the provider), for refund reconciliation.</summary>
    Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken);
}
