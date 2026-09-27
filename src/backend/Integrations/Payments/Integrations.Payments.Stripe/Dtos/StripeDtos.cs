using System.Text.Json.Serialization;

namespace TravelBooking.Integrations.Payments.Stripe.Dtos;

// Stripe wire model (snake_case JSON): the subset the adapter maps, modelled from Stripe's public API reference
// (Payment Intents, Refunds, Search, Events, Errors). Internal to this adapter (ADR 0004); verify against test-mode
// responses before the adapter is marked production-ready.

internal sealed record StripePaymentIntent(
    string Id,
    long Amount,
    [property: JsonPropertyName("amount_capturable")] long AmountCapturable,
    [property: JsonPropertyName("amount_received")] long AmountReceived,
    string Currency,
    string Status,
    Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("client_secret")] string? ClientSecret,
    [property: JsonPropertyName("cancellation_reason")] string? CancellationReason,
    [property: JsonPropertyName("last_payment_error")] StripeErrorDetail? LastPaymentError,
    [property: JsonPropertyName("latest_charge")] StripeCharge? LatestCharge,
    bool Livemode);

/// <summary>The PaymentIntent's latest charge, expanded (<c>expand[]=latest_charge</c>).</summary>
internal sealed record StripeCharge(
    string Id,
    string Status,
    bool Captured,
    [property: JsonPropertyName("amount_refunded")] long AmountRefunded);

internal sealed record StripeRefund(
    string Id,
    long Amount,
    string Currency,
    string Status,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntent,
    Dictionary<string, string>? Metadata);

internal sealed record StripeList<T>(List<T> Data);

internal sealed record StripeErrorResponse(StripeErrorDetail? Error);

internal sealed record StripeErrorDetail(
    string? Type,
    string? Code,
    [property: JsonPropertyName("decline_code")] string? DeclineCode,
    [property: JsonPropertyName("payment_intent")] StripePaymentIntent? PaymentIntent);

internal sealed record StripeEvent(string Id, string Type, bool Livemode, long Created, StripeEventData? Data);

internal sealed record StripeEventData(StripeEventObject? Object);

/// <summary>Only the fields the core needs from an event's object: the rest (which can hold personal data) is not read.</summary>
internal sealed record StripeEventObject(
    string? Id,
    [property: JsonPropertyName("object")] string? ObjectType,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntent,
    Dictionary<string, string>? Metadata);
