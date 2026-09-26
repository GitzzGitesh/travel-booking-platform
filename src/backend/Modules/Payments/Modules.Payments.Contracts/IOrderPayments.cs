using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Contracts;

/// <summary>
/// What other modules may ask of Payments (ADR 0002: Contracts only). Orders uses it to authorize the order's total
/// before any supplier booking (ADR 0005: authorize → book → capture). Provider types never cross this boundary.
/// </summary>
public interface IOrderPayments
{
    /// <summary>
    /// Authorizes (manual capture) the order's server-side total, as one payment attempt identified by the order and our
    /// idempotency key. Repeating the same request returns the same attempt: an attempt whose outcome was unknown is looked
    /// up with the provider, never authorized again. A declined or failed attempt is final; the customer retries with a
    /// new key (a new attempt).
    /// </summary>
    Task<Result<OrderPaymentResult, OrderPaymentFailure>> AuthorizeAsync(OrderPaymentRequest request, CancellationToken cancellationToken);
}

/// <param name="PaymentMethodToken">The provider's opaque token from its hosted card fields; never card data.</param>
public sealed record OrderPaymentRequest(Guid OrderId, string CustomerId, string IdempotencyKey, Money Amount, string PaymentMethodToken);

/// <param name="PaymentId">Our payment attempt id: the authorization reference Orders records (ADR 0005).</param>
/// <param name="CustomerAction">What the customer's browser needs for a challenge (SCA); only for the order's owner.</param>
public sealed record OrderPaymentResult(Guid PaymentId, OrderPaymentStatus Status, string? DeclineReason = null, string? CustomerAction = null);

public enum OrderPaymentStatus
{
    /// <summary>The total is held: the booking may start.</summary>
    Authorized,

    /// <summary>The customer must complete a challenge; repeat the request with the same key afterwards.</summary>
    ActionRequired,

    /// <summary>Refused by the card issuer or provider; nothing held. Another attempt needs a new key.</summary>
    Declined,

    /// <summary>The outcome is not known yet (a timeout): repeat the request with the same key later. Never book on it.</summary>
    Pending,

    /// <summary>Nothing is held (cancelled, expired, or confirmed absent after the provider's consistency window).</summary>
    Failed,

    /// <summary>The provider reported something other than what was asked: a person must look at it.</summary>
    ManualReview,
}

public enum OrderPaymentFailure
{
    /// <summary>The key was already used for this order with another amount.</summary>
    IdempotencyKeyReused,

    /// <summary>The token is not a usable provider token (for example, it looks like a card number).</summary>
    InvalidPaymentMethod,

    /// <summary>
    /// Another attempt for this order (another key) may still hold funds: it must be completed with its own key, or
    /// found to hold nothing, before a new one starts (F-32).
    /// </summary>
    PaymentInProgress,

    InvalidRequest,
}
