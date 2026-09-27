using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.Integrations.Payments.Stripe.Dtos;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Stripe;

/// <summary>
/// Stripe webhook verification, as Stripe documents it for manual verification. The <c>Stripe-Signature</c> header
/// carries a timestamp (<c>t=</c>) and one or more <c>v1=</c> signatures: HMAC-SHA256, keyed by the endpoint's signing
/// secret, over "{timestamp}.{raw body}". Other schemes are ignored (no downgrade). Signatures are compared in constant
/// time, and an event older (or newer) than the tolerance is refused as a possible replay. Only then is the body read,
/// and only for the event id, its type and the payment it concerns. A test-mode key accepts only test-mode events.
/// </summary>
internal sealed class StripeNotifications(IOptions<StripeOptions> options, TimeProvider timeProvider) : IPaymentNotifications
{
    public const string SignatureHeader = "Stripe-Signature";

    public string ProviderId => StripePaymentProvider.ProviderId;

    public Result<PaymentNotification, PaymentNotificationRejection> Verify(string body, IReadOnlyDictionary<string, string> headers)
    {
        var settings = options.Value;
        if (!headers.TryGetValue(SignatureHeader, out var header) || Parse(header) is not { } signed)
        {
            return Reject(PaymentNotificationRejection.InvalidSignature);
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(settings.WebhookSigningSecret!),
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{signed.Timestamp}.{body}")));
        if (!signed.Signatures.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
        {
            return Reject(PaymentNotificationRejection.InvalidSignature);
        }

        var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(signed.Timestamp);
        if (age.Duration() > settings.WebhookTolerance)
        {
            return Reject(PaymentNotificationRejection.Stale);
        }

        return Read(body);
    }

    private static Result<PaymentNotification, PaymentNotificationRejection> Read(string body)
    {
        StripeEvent? stripeEvent;
        try
        {
            stripeEvent = JsonSerializer.Deserialize<StripeEvent>(body, StripePaymentProvider.Json);
        }
        catch (JsonException)
        {
            return Reject(PaymentNotificationRejection.Malformed);
        }

        // Only test-mode events while the adapter holds a test key (StripeOptions refuses live keys).
        if (stripeEvent is not { Id.Length: > 0 and <= PaymentNotification.MaxEventIdLength, Type.Length: > 0 and <= PaymentNotification.MaxEventTypeLength, Livemode: false }
            || stripeEvent.Created is < 0 or > 253402300799)
        {
            return Reject(PaymentNotificationRejection.Malformed);
        }

        var target = stripeEvent.Data?.Object;
        var kind = stripeEvent.Type switch
        {
            _ when stripeEvent.Type.StartsWith("payment_intent.", StringComparison.Ordinal) && target?.ObjectType == "payment_intent" => PaymentNotificationKind.Payment,
            "charge.refunded" or "refund.created" or "refund.updated" or "refund.failed" => PaymentNotificationKind.Refund,
            _ => PaymentNotificationKind.Ignored,
        };

        if (kind is PaymentNotificationKind.Ignored)
        {
            return Result<PaymentNotification, PaymentNotificationRejection>.Success(new PaymentNotification(stripeEvent.Id, kind, null, null, stripeEvent.Type));
        }

        var paymentIntentId = kind is PaymentNotificationKind.Payment ? target?.Id : target?.PaymentIntent;
        var reference = target?.Metadata?.GetValueOrDefault(StripePaymentProvider.ReferenceMetadata);
        return Result<PaymentNotification, PaymentNotificationRejection>.Success(new PaymentNotification(
            stripeEvent.Id,
            kind,
            reference is not null && IsReference(reference) ? new PaymentReference(reference) : null,
            paymentIntentId is { Length: > 0 and <= 255 } ? new ProviderPaymentRef(StripePaymentProvider.ProviderId, paymentIntentId) : null,
            stripeEvent.Type,
            DateTimeOffset.FromUnixTimeSeconds(stripeEvent.Created)));
    }

    private static SignedHeader? Parse(string header)
    {
        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            var (key, value) = (pair[0].Trim(), pair[1].Trim());
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                timestamp = seconds;
            }
            else if (key == "v1" && value.Length == 64 && value.All(char.IsAsciiHexDigit))
            {
                signatures.Add(Convert.FromHexString(value));
            }
        }

        return timestamp is { } t && t <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() && signatures.Count > 0 ? new SignedHeader(t, signatures) : null;
    }

    private static bool IsReference(string value) =>
        value.Length is > 0 and <= PaymentReference.MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');

    private static Result<PaymentNotification, PaymentNotificationRejection> Reject(PaymentNotificationRejection rejection) =>
        Result<PaymentNotification, PaymentNotificationRejection>.Failure(rejection);

    private sealed record SignedHeader(long Timestamp, List<byte[]> Signatures);
}
