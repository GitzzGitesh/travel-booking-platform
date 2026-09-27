using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;

namespace TravelBooking.Modules.Payments.Ports;

/// <summary>
/// The payment provider port (ADR 0004): provider-neutral. ADR 0006 recommends Stripe as the first real provider, with
/// server-side confirmation of a browser-created token; the core never depends on it. Implemented by Integrations.Payments.* adapters, which convert amounts to provider
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
    /// Whether this adapter has passed its provider's test-mode contract suite and has production access (ADR 0006).
    /// Outside Development and Staging, startup refuses a provider that is not. Default: not ready (fail closed).
    /// </summary>
    bool IsProductionReady => false;

    /// <summary>
    /// The shortest "not found" window this provider's lookup by our reference needs (its search can lag its writes):
    /// startup refuses a shorter Payments:Reconciliation:NotFoundConclusiveAfter. Zero: consistent at once.
    /// </summary>
    TimeSpan MinimumNotFoundWindow => TimeSpan.Zero;

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

    /// <summary>
    /// The same lookup when the provider's payment id is already known (e.g. after a challenge). A provider whose search
    /// by our reference lags its writes reads the payment directly instead, which is consistent at once. The adapter
    /// still reports the payment's own reference, so the core detects a payment that is not ours. By default, the
    /// lookup by our reference.
    /// </summary>
    Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, ProviderPaymentRef? knownPayment, CancellationToken cancellationToken) =>
        RetrieveAsync(reference, cancellationToken);

    /// <summary>Looks one refund up by OUR key (the adapter stores it with the provider), for refund reconciliation.</summary>
    Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken);
}
