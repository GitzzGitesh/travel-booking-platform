using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Ports;

/// <summary>
/// OUR stable reference for one payment ATTEMPT (the Payment record's id): an order may need several, e.g. another card
/// after a decline (F-20). It is the authorization's idempotency key at the provider and the key for finding the
/// payment again after an unknown outcome.
/// </summary>
public readonly record struct PaymentReference
{
    public const int MaxLength = 64;

    public PaymentReference(string value)
    {
        if (!Identifiers.IsValid(value, MaxLength))
        {
            throw new ArgumentException($"A payment reference is 1 to {MaxLength} letters, digits, hyphens or colons.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The idempotency key of one write after authorization, derived from our ids: one capture and one void per payment
/// ("{paymentReference}:capture", "{paymentReference}:void"), one key per refund ("{refundId}:refund"). The same key must
/// always mean the same operation. Providers keep keys only for a limited window, so a repeat long after an unknown
/// outcome must be preceded by a lookup (payment-lifecycle.md).
/// </summary>
public readonly record struct OperationKey
{
    public const int MaxLength = 100;

    public OperationKey(string value)
    {
        if (!Identifiers.IsValid(value, MaxLength))
        {
            throw new ArgumentException($"An operation key is 1 to {MaxLength} letters, digits, hyphens or colons.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// An opaque token for the customer's payment method, created by the provider's hosted card fields in the browser
/// (PCI SAQ-A, security rules). Card numbers never reach our servers: anything that looks like one is refused here as a
/// last line of defence; each adapter also checks its provider's own token format (an allow-list).
/// </summary>
public readonly record struct PaymentMethodToken
{
    public const int MaxLength = 255;

    public PaymentMethodToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            throw new ArgumentException($"A payment method token is 1 to {MaxLength} characters.", nameof(value));
        }

        if (ContainsCardNumber(value))
        {
            // Never echo the value: it may be a card number.
            throw new ArgumentException("A payment method token must be a provider token, never a card number.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    /// <summary>Never prints the token.</summary>
    public override string ToString() => "payment method token";

    // A group of 13 to 19 digits, whatever the separators inside it (whitespace, hyphens, dots) and wherever it appears,
    // that passes the Luhn check. Luhn keeps most all-digit provider tokens usable.
    private static bool ContainsCardNumber(string value)
    {
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '.')).ToArray());
        foreach (var group in compact.Split(c => !char.IsAsciiDigit(c)))
        {
            if (group.Length is >= 13 and <= 19 && PassesLuhn(group))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                digit = digit * 2 > 9 ? (digit * 2) - 9 : digit * 2;
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }
}

/// <summary>
/// What the customer's browser needs to complete a challenge (e.g. SCA). It can confirm the payment, so it is only ever
/// returned to the order's owner and never printed.
/// </summary>
public readonly record struct CustomerActionToken(string Value)
{
    public override string ToString() => "customer action token";
}

/// <summary>The provider's own id for a payment (e.g. a payment intent): opaque to the core, stored verbatim.</summary>
public sealed record ProviderPaymentRef(string ProviderId, string Value);

/// <summary>Where a payment stands at the provider. Refunds do not change it; see <see cref="PaymentSnapshot.Refunded"/>.</summary>
public enum PaymentState
{
    /// <summary>The customer must complete a challenge (e.g. SCA) before the payment is authorized.</summary>
    RequiresAction,

    /// <summary>The amount is held on the card, not taken (manual capture).</summary>
    Authorized,

    /// <summary>The issuer or provider refused the payment method; nothing is held. Use a new reference to try again.</summary>
    Declined,

    /// <summary>The challenge was abandoned or failed, or the provider cancelled the payment; nothing is held (F-21).</summary>
    Canceled,

    /// <summary>The hold lapsed before capture; nothing can be taken (F-24).</summary>
    Expired,

    /// <summary>Money was taken (once per payment).</summary>
    Captured,

    /// <summary>We released the hold before capture; nothing was taken.</summary>
    Voided,
}

public enum PaymentDeclineReason
{
    Generic,
    InsufficientFunds,
    ExpiredCard,
}

/// <summary>A payment as the provider reports it. Amounts share the authorized amount's currency.</summary>
public sealed record PaymentSnapshot(
    PaymentReference Reference,
    ProviderPaymentRef Payment,
    PaymentState State,
    Money Amount,
    Money Captured,
    Money Refunded,
    PaymentDeclineReason? DeclineReason = null,
    CustomerActionToken? CustomerActionToken = null);

/// <summary>A lookup by our reference: <see cref="Payment"/> is null when the provider definitely has no such payment.</summary>
public sealed record PaymentLookup(PaymentSnapshot? Payment)
{
    public bool Found => Payment is not null;
}

/// <summary>The provider's own id for a refund: opaque to the core.</summary>
public sealed record ProviderRefundRef(string ProviderId, string Value);

public enum RefundStatus
{
    /// <summary>Accepted, not yet final: it can still fail later (the provider reports it asynchronously).</summary>
    Pending,

    Succeeded,

    /// <summary>The provider could not return the money (F-43): nothing was refunded.</summary>
    Failed,
}

/// <summary>One refund, identified by OUR key: its own amount and status (refunds are separate records, ADR 0005).</summary>
public sealed record PaymentRefund(PaymentReference Payment, OperationKey Key, ProviderRefundRef Refund, Money Amount, RefundStatus Status);

/// <summary>A refund lookup by our key: <see cref="Refund"/> is null when the provider definitely has no such refund.</summary>
public sealed record RefundLookup(PaymentRefund? Refund)
{
    public bool Found => Refund is not null;
}

/// <summary>Authorize (manual capture) <see cref="Amount"/>, the server-side order total, never a client amount.</summary>
public sealed record AuthorizationDetails(PaymentReference Reference, Money Amount, PaymentMethodToken PaymentMethod);

/// <summary>Take <see cref="Amount"/>, once per payment: at most the authorized amount (less for a partially confirmed order).</summary>
public sealed record CaptureDetails(PaymentReference Reference, ProviderPaymentRef Payment, OperationKey Key, Money Amount);

/// <summary>Release the hold. Only before capture.</summary>
public sealed record VoidDetails(PaymentReference Reference, ProviderPaymentRef Payment, OperationKey Key);

/// <summary>Return <see cref="Amount"/> of what was captured; the total refunded never exceeds the captured amount.</summary>
public sealed record RefundDetails(PaymentReference Reference, ProviderPaymentRef Payment, OperationKey Key, Money Amount);

internal static class Identifiers
{
    public static bool IsValid(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');
}

internal static class StringSplitting
{
    public static IEnumerable<string> Split(this string value, Func<char, bool> isSeparator)
    {
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || isSeparator(value[i]))
            {
                if (i > start)
                {
                    yield return value[start..i];
                }

                start = i + 1;
            }
        }
    }
}
