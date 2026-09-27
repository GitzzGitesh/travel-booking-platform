using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Ports;

/// <summary>
/// Verifies and reads a payment provider's notification (webhook), implemented by the provider's adapter (ADR 0006).
/// A notification is only a prompt: the core never takes a payment's state from it. It records the event once, by the
/// provider's event id, and later looks the payment up with the provider. That lookup is what changes the attempt, so a
/// duplicate, late or out-of-order notification changes nothing on its own.
/// </summary>
public interface IPaymentNotifications
{
    /// <summary>The <see cref="IPaymentProvider.Id"/> these notifications come from.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Checks the signature (and its age) over the exact raw body, then reads the few fields the core needs. Nothing
    /// is trusted before the signature is verified. Never throws for bad input.
    /// </summary>
    /// <param name="headers">The request headers, by case-insensitive name.</param>
    Result<PaymentNotification, PaymentNotificationRejection> Verify(string body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>What a verified notification is about. The rest of the provider's event is not kept.</summary>
public enum PaymentNotificationKind
{
    /// <summary>A payment changed (authorized, challenged, declined, canceled, captured...): look it up.</summary>
    Payment,

    /// <summary>A refund changed: looked up with refund reconciliation, when refunds are persisted.</summary>
    Refund,

    /// <summary>An event type the core does not act on. It is acknowledged and not stored.</summary>
    Ignored,
}

/// <param name="EventId">The provider's unique event id: the deduplication key.</param>
/// <param name="Reference">Our reference, when the provider echoes it (it is re-checked by the lookup).</param>
/// <param name="Payment">The provider's payment id, when the event carries one: a lookup hint, checked against our reference.</param>
/// <param name="EventType">The provider's event type, for operators (e.g. which event arrived late).</param>
/// <param name="OccurredAt">When the provider created the event, for operators; never used to order processing.</param>
public sealed record PaymentNotification(
    string EventId, PaymentNotificationKind Kind, PaymentReference? Reference, ProviderPaymentRef? Payment, string? EventType = null, DateTimeOffset? OccurredAt = null)
{
    public const int MaxEventIdLength = 255;
    public const int MaxEventTypeLength = 100;
}

public enum PaymentNotificationRejection
{
    /// <summary>No valid signature for our secret: not from the provider. A security event.</summary>
    InvalidSignature,

    /// <summary>Correctly signed, but older than the allowed tolerance: a possible replay.</summary>
    Stale,

    /// <summary>Signed, but not a notification the adapter can read.</summary>
    Malformed,
}
