using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Integrations.Payments.Stripe.Dtos;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Stripe;

/// <summary>
/// Stripe adapter (ADR 0006), mapped from Stripe's public documentation and tested against documentation-shaped
/// fixtures only: NOT production-ready. One PaymentIntent per payment attempt, with manual capture, created and
/// confirmed in one call on our server with the token the browser's Payment Element made (a ConfirmationToken, or a
/// PaymentMethod in Stripe's test mode). A challenge (3DS) comes back as RequiresAction with the client secret, which only
/// the order's owner's browser receives, to run it with Stripe.js. Its result is then read by a lookup, never taken
/// from the browser.
/// <para>
/// Idempotency: our payment reference is the Idempotency-Key of the authorization and is stored as metadata, so the
/// payment can be found by it; later writes use our operation keys. Stripe keeps keys for at least 24 hours and replays
/// the first result, a 500 included, so an unknown outcome is always looked up, never re-sent under a new key.
/// Amounts are converted to minor units here, only for two-decimal currencies, and never rounded (ADR 0010).
/// </para>
/// </summary>
internal sealed partial class StripePaymentProvider(IHttpClientFactory httpClients, IOptions<StripeOptions> options, ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    public const string ProviderId = "stripe";
    public const string HttpClientName = "Integrations.Payments.Stripe";

    internal const string ReferenceMetadata = "reference";
    internal const string OperationKeyMetadata = "operation_key";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // Stands in for a payment whose metadata does not carry a reference of ours: the core then sees a mismatch.
    private static readonly PaymentReference _notOurs = new("not-ours");

    public string Id => ProviderId;

    public bool IsProductionReady => false;

    /// <summary>
    /// Stripe's search normally lags writes by under a minute, but can lag much more during incidents, so "not found" by
    /// our reference is concluded only after an hour (ADR 0006).
    /// </summary>
    public TimeSpan MinimumNotFoundWindow => TimeSpan.FromHours(1);

    public async Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken)
    {
        if (ToMinorUnits(details.Amount) is not { } amount)
        {
            return Refused<PaymentSnapshot>($"Stripe is not configured for {details.Amount.Currency}, or the amount has more than two decimals.");
        }

        if (MethodField(details.PaymentMethod, options.Value.IsTestMode) is not { } method)
        {
            return Refused<PaymentSnapshot>("Not a Stripe confirmation token or payment method.");
        }

        var response = await SendAsync(HttpMethod.Post, "v1/payment_intents", details.Reference.Value, SupplierCallKind.Write, cancellationToken,
        [
            new("amount", amount.ToString(CultureInfo.InvariantCulture)),
            new("currency", details.Amount.Currency.Value.ToLowerInvariant()),
            new("capture_method", "manual"),
            new("confirm", "true"),
            new("payment_method_types[0]", "card"), // cards only: every card supports manual capture
            new(method, details.PaymentMethod.Value),
            new($"metadata[{ReferenceMetadata}]", details.Reference.Value),
            new("expand[0]", "latest_charge"),
        ]);

        return await IntentAsync(response, "authorize", SupplierCallKind.Write, cancellationToken);
    }

    public async Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken)
    {
        if (!IsOurs(details.Payment) || ToMinorUnits(details.Amount) is not { } amount)
        {
            return Refused<PaymentSnapshot>("Not a Stripe payment, or an unsupported amount.");
        }

        var response = await SendAsync(HttpMethod.Post, $"v1/payment_intents/{Uri.EscapeDataString(details.Payment.Value)}/capture", details.Key.Value, SupplierCallKind.Write, cancellationToken,
        [
            new("amount_to_capture", amount.ToString(CultureInfo.InvariantCulture)),
            new("expand[0]", "latest_charge"),
        ]);

        return await IntentAsync(response, "capture", SupplierCallKind.Write, cancellationToken);
    }

    public async Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken)
    {
        if (!IsOurs(details.Payment))
        {
            return Refused<PaymentSnapshot>("Not a Stripe payment.");
        }

        var response = await SendAsync(HttpMethod.Post, $"v1/payment_intents/{Uri.EscapeDataString(details.Payment.Value)}/cancel", details.Key.Value, SupplierCallKind.Write, cancellationToken,
        [
            new("cancellation_reason", "abandoned"),
            new("expand[0]", "latest_charge"),
        ]);

        // Already canceled (the hold lapsed, or an earlier close or void): read it, and let its state say what happened.
        if (response.IsSuccess && response.Value.Status == HttpStatusCode.BadRequest && ErrorOf(response.Value.Body) is { Code: "payment_intent_unexpected_state" })
        {
            return await ReadAsync(details.Payment, cancellationToken);
        }

        return await IntentAsync(response, "void", SupplierCallKind.Write, cancellationToken);
    }

    public async Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken)
    {
        if (!IsOurs(details.Payment) || ToMinorUnits(details.Amount) is not { } amount)
        {
            return Refused<PaymentRefund>("Not a Stripe payment, or an unsupported amount.");
        }

        var response = await SendAsync(HttpMethod.Post, "v1/refunds", details.Key.Value, SupplierCallKind.Write, cancellationToken,
        [
            new("payment_intent", details.Payment.Value),
            new("amount", amount.ToString(CultureInfo.InvariantCulture)),
            new($"metadata[{ReferenceMetadata}]", details.Reference.Value),
            new($"metadata[{OperationKeyMetadata}]", details.Key.Value),
        ]);

        if (!response.IsSuccess)
        {
            return Result<PaymentRefund, ProviderError>.Failure(response.Error);
        }

        if (!IsSuccess(response.Value.Status))
        {
            return Result<PaymentRefund, ProviderError>.Failure(Error(response.Value, "refund", SupplierCallKind.Write));
        }

        return Map(response.Value, "refund", body => ToRefund(Deserialize<StripeRefund>(body)));
    }

    /// <summary>
    /// By our reference: Stripe's search by metadata. Search lags writes (normally under a minute), so "not found" is
    /// conclusive only after the reconciliation window (Payments:Reconciliation:NotFoundConclusiveAfter). It is not
    /// available to accounts in India.
    /// </summary>
    public Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken) =>
        SearchAsync(reference, cancellationToken);

    /// <summary>With the PaymentIntent id known: a direct read, consistent at once.</summary>
    public async Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, ProviderPaymentRef? knownPayment, CancellationToken cancellationToken)
    {
        if (knownPayment is null || !IsOurs(knownPayment))
        {
            return await RetrieveAsync(reference, cancellationToken);
        }

        var intent = await ReadAsync(knownPayment, cancellationToken);
        return intent.IsSuccess
            ? Result<PaymentLookup, ProviderError>.Success(new PaymentLookup(intent.Value))
            : Result<PaymentLookup, ProviderError>.Failure(intent.Error);
    }

    private async Task<Result<PaymentSnapshot, ProviderError>> ReadAsync(ProviderPaymentRef payment, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, $"v1/payment_intents/{Uri.EscapeDataString(payment.Value)}?expand[]=latest_charge", null, SupplierCallKind.Read, cancellationToken);
        if (response.IsSuccess && response.Value.Status == HttpStatusCode.NotFound)
        {
            // Our stored id is unknown to this account (a key for another account or mode?): unknown, never "absent".
            return Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "The stored PaymentIntent id is not found with this key."));
        }

        return await IntentAsync(response, "lookup", SupplierCallKind.Read, cancellationToken);
    }

    public async Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken)
    {
        var payment = await SearchAsync(reference, cancellationToken);
        if (!payment.IsSuccess)
        {
            return Result<RefundLookup, ProviderError>.Failure(payment.Error);
        }

        if (payment.Value.Payment is null)
        {
            return Result<RefundLookup, ProviderError>.Success(new RefundLookup(null));
        }

        var response = await SendAsync(HttpMethod.Get, $"v1/refunds?payment_intent={Uri.EscapeDataString(payment.Value.Payment.Payment.Value)}&limit=100", null, SupplierCallKind.Read, cancellationToken);
        if (!response.IsSuccess)
        {
            return Result<RefundLookup, ProviderError>.Failure(response.Error);
        }

        if (!IsSuccess(response.Value.Status))
        {
            return Result<RefundLookup, ProviderError>.Failure(Error(response.Value, "refund lookup", SupplierCallKind.Read));
        }

        return Map(response.Value, "refund lookup", body =>
        {
            var refund = Deserialize<StripeList<StripeRefund>>(body).Data
                .SingleOrDefault(r => r.Metadata?.GetValueOrDefault(OperationKeyMetadata) == key.Value);
            return new RefundLookup(refund is null ? null : ToRefund(refund));
        });
    }

    private async Task<Result<PaymentLookup, ProviderError>> SearchAsync(PaymentReference reference, CancellationToken cancellationToken)
    {
        // Our references are letters, digits, hyphens and colons only: nothing to escape inside the quotes.
        var query = Uri.EscapeDataString($"metadata['{ReferenceMetadata}']:'{reference.Value}'");
        var response = await SendAsync(HttpMethod.Get, $"v1/payment_intents/search?query={query}&expand[]=data.latest_charge", null, SupplierCallKind.Read, cancellationToken);
        if (!response.IsSuccess)
        {
            return Result<PaymentLookup, ProviderError>.Failure(response.Error);
        }

        if (!IsSuccess(response.Value.Status))
        {
            return Result<PaymentLookup, ProviderError>.Failure(Error(response.Value, "lookup", SupplierCallKind.Read));
        }

        List<StripePaymentIntent> found;
        try
        {
            found = Deserialize<StripeList<StripePaymentIntent>>(response.Value.Body).Data;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            LogUnmappable(logger, "lookup", exception.GetType().Name);
            return Result<PaymentLookup, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Unexpected Stripe search response."));
        }

        if (found.Count > 1)
        {
            // Our reference is the authorization's idempotency key: two payments for it need a person, not a guess.
            LogSeveralPayments(logger, reference.Value, found.Count);
            return Result<PaymentLookup, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Several Stripe payments carry this reference."));
        }

        if (found.Count == 0)
        {
            return Result<PaymentLookup, ProviderError>.Success(new PaymentLookup(null));
        }

        var settled = await SettleAsync(found[0], cancellationToken);
        return settled.IsSuccess
            ? Result<PaymentLookup, ProviderError>.Success(new PaymentLookup(settled.Value))
            : Result<PaymentLookup, ProviderError>.Failure(settled.Error);
    }

    /// <summary>
    /// Maps a PaymentIntent response. A card decline is a 402 that still carries the PaymentIntent: a Declined payment,
    /// not an error.
    /// </summary>
    private async Task<Result<PaymentSnapshot, ProviderError>> IntentAsync(Result<SupplierResponse, ProviderError> response, string operation, SupplierCallKind kind, CancellationToken cancellationToken)
    {
        if (!response.IsSuccess)
        {
            return Result<PaymentSnapshot, ProviderError>.Failure(response.Error);
        }

        var answer = response.Value;
        StripePaymentIntent intent;
        if (IsSuccess(answer.Status))
        {
            var read = Map(answer, operation, Deserialize<StripePaymentIntent>);
            if (!read.IsSuccess)
            {
                return Result<PaymentSnapshot, ProviderError>.Failure(read.Error);
            }

            intent = read.Value;
        }
        else if (answer.Status == HttpStatusCode.PaymentRequired && ErrorOf(answer.Body) is { Type: "card_error", PaymentIntent: { } declined })
        {
            intent = declined;
        }
        else
        {
            return Result<PaymentSnapshot, ProviderError>.Failure(Error(answer, operation, kind));
        }

        return await SettleAsync(intent, cancellationToken);
    }

    /// <summary>
    /// The PaymentIntent in the port's terms. A declined one (<c>requires_payment_method</c>) is not final at Stripe: its
    /// client secret could still confirm it with another card, holding funds on an attempt we consider over. So it is
    /// closed (canceled, keyed by our reference) before it is reported Declined; if that is not certain, the answer is
    /// unknown and the next lookup tries again. Never for a payment that is not ours.
    /// </summary>
    private async Task<Result<PaymentSnapshot, ProviderError>> SettleAsync(StripePaymentIntent intent, CancellationToken cancellationToken)
    {
        if (ToSnapshot(intent) is not { } snapshot)
        {
            LogUnmappable(logger, "mapping", intent.Status);
            return Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "The Stripe payment is not in a state the adapter maps."));
        }

        if (intent.Status != "requires_payment_method" || snapshot.Reference == _notOurs || !IsOurs(snapshot.Payment))
        {
            return Result<PaymentSnapshot, ProviderError>.Success(snapshot);
        }

        var closed = await SendAsync(HttpMethod.Post, $"v1/payment_intents/{Uri.EscapeDataString(intent.Id)}/cancel", $"{snapshot.Reference.Value}:close-declined", SupplierCallKind.Write, cancellationToken,
        [
            new("cancellation_reason", "abandoned"),
        ]);
        if (closed.IsSuccess && IsSuccess(closed.Value.Status))
        {
            return Result<PaymentSnapshot, ProviderError>.Success(snapshot);
        }

        LogFailure(logger, "close declined", ProviderErrorKind.Unknown, closed.IsSuccess ? $"HTTP {(int)closed.Value.Status}" : closed.Error.Message);
        return Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "Declined, but the PaymentIntent is not closed yet: look it up again."));
    }

    /// <summary>The PaymentIntent in the port's terms; null when its currency or state is not one this adapter maps.</summary>
    internal static PaymentSnapshot? ToSnapshot(StripePaymentIntent intent)
    {
        var currency = intent.Currency.ToUpperInvariant();
        if (!StripeOptions.TwoDecimalCurrencies.Contains(currency))
        {
            return null;
        }

        var charge = intent.LatestCharge;
        var authorized = charge is { Status: "succeeded" };
        PaymentState? state = intent.Status switch
        {
            "requires_capture" => PaymentState.Authorized,
            "requires_action" => PaymentState.RequiresAction,
            "requires_payment_method" => PaymentState.Declined, // a failed confirmation: a decline, or failed authentication
            "succeeded" => PaymentState.Captured,

            // A hold that existed was either released by us (Voided) or lapsed (Expired, F-24); none: nothing was ever held.
            "canceled" when authorized && intent.CancellationReason is "automatic" or "expired" => PaymentState.Expired,
            "canceled" when authorized => PaymentState.Voided,
            "canceled" when intent.LastPaymentError is not null => PaymentState.Declined, // a decline we closed
            "canceled" => PaymentState.Canceled,
            _ => null, // processing, requires_confirmation: not settled yet, looked up again later
        };

        if (state is not { } known)
        {
            return null;
        }

        var reference = intent.Metadata?.GetValueOrDefault(ReferenceMetadata) is { } value && IsReference(value) ? new PaymentReference(value) : _notOurs;
        var code = new CurrencyCode(currency);
        return new PaymentSnapshot(
            reference,
            new ProviderPaymentRef(ProviderId, intent.Id),
            known,
            // Authorized: what can actually be captured (a partial authorization then differs from the amount asked).
            FromMinorUnits(known is PaymentState.Authorized ? intent.AmountCapturable : intent.Amount, code),
            FromMinorUnits(known is PaymentState.Captured ? intent.AmountReceived : 0, code),
            FromMinorUnits(charge is { Captured: true } ? charge.AmountRefunded : 0, code),
            known is PaymentState.Declined ? DeclineReason(intent.LastPaymentError) : null,
            known is PaymentState.RequiresAction && intent.ClientSecret is { Length: > 0 } secret ? new CustomerActionToken(secret) : null);
    }

    private static PaymentDeclineReason DeclineReason(StripeErrorDetail? error) => (error?.DeclineCode ?? error?.Code) switch
    {
        "insufficient_funds" => PaymentDeclineReason.InsufficientFunds,
        "expired_card" => PaymentDeclineReason.ExpiredCard,
        _ => PaymentDeclineReason.Generic,
    };

    private static PaymentRefund ToRefund(StripeRefund refund)
    {
        var currency = refund.Currency.ToUpperInvariant();
        if (!StripeOptions.TwoDecimalCurrencies.Contains(currency)
            || refund.Metadata?.GetValueOrDefault(ReferenceMetadata) is not { } reference || !IsReference(reference)
            || refund.Metadata.GetValueOrDefault(OperationKeyMetadata) is not { } key || !IsOperationKey(key))
        {
            throw new InvalidOperationException("A Stripe refund without our reference and key, or in an unmapped currency.");
        }

        var status = refund.Status switch
        {
            "succeeded" => RefundStatus.Succeeded,
            "failed" or "canceled" => RefundStatus.Failed,
            _ => RefundStatus.Pending, // pending, requires_action: not final
        };
        return new PaymentRefund(new PaymentReference(reference), new OperationKey(key), new ProviderRefundRef(ProviderId, refund.Id), FromMinorUnits(refund.Amount, new CurrencyCode(currency)), status);
    }

    /// <summary>
    /// A Stripe error in the shared taxonomy. Codes only: Stripe's messages are never copied. Idempotency errors are
    /// told apart by code: our key reused with other parameters is definitive, and a key still in use is unknown.
    /// </summary>
    private ProviderError Error(SupplierResponse answer, string operation, SupplierCallKind kind)
    {
        var error = ErrorOf(answer.Body);
        var errorKind = error switch
        {
            { Code: "idempotency_key_in_use" } => ProviderErrorKind.OperationInProgress,
            { Type: "idempotency_error" } => ProviderErrorKind.IdempotencyConflict,

            // Any other conflict on a write proves nothing about what Stripe did: unknown, looked up.
            _ when answer.Status == HttpStatusCode.Conflict && kind == SupplierCallKind.Write => ProviderErrorKind.Unknown,
            _ => SupplierHttp.Classify(answer.Status, kind),
        };

        var detail = $"HTTP {(int)answer.Status}, Stripe {error?.Type ?? "unknown"}/{error?.Code ?? "none"}";
        LogFailure(logger, operation, errorKind, detail);
        return new ProviderError(errorKind, detail);
    }

    private static StripeErrorDetail? ErrorOf(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<StripeErrorResponse>(body, Json)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Result<T, ProviderError> Map<T>(SupplierResponse answer, string operation, Func<string, T> map)
        where T : notnull
    {
        try
        {
            return Result<T, ProviderError>.Success(map(answer.Body));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // On a write this is still an answer from Stripe, but one we cannot read: the outcome is unknown.
            LogUnmappable(logger, operation, exception.GetType().Name);
            return Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Unexpected Stripe response."));
        }
    }

    private async Task<Result<SupplierResponse, ProviderError>> SendAsync(
        HttpMethod method, string path, string? idempotencyKey, SupplierCallKind kind, CancellationToken cancellationToken, IEnumerable<KeyValuePair<string, string>>? form = null)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = SupplierHttp.Bearer(settings.SecretKey!);
        request.Headers.Add("Stripe-Version", settings.ApiVersion);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        var timeout = kind == SupplierCallKind.Write ? settings.WriteTimeout : settings.ReadTimeout;
        return await SupplierHttp.SendRawAsync(httpClients.CreateClient(HttpClientName), request, timeout, kind, cancellationToken);
    }

    /// <summary>Amount in minor units, or null for a currency not configured (or not two-decimal) or a fraction of a cent.</summary>
    private long? ToMinorUnits(Money money)
    {
        if (!options.Value.Currencies.Contains(money.Currency.Value) || !StripeOptions.TwoDecimalCurrencies.Contains(money.Currency.Value))
        {
            return null;
        }

        var minor = money.Amount * 100m;
        return minor > 0 && minor == decimal.Truncate(minor) && minor <= 99_999_999m ? (long)minor : null;
    }

    private static Money FromMinorUnits(long amount, CurrencyCode currency) => new(amount / 100m, currency);

    /// <summary>The allow-list of Stripe tokens: which form field carries it, or null when it is not a Stripe token.</summary>
    private static string? MethodField(PaymentMethodToken token, bool testMode)
    {
        var value = token.Value;
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return null;
        }

        return value.StartsWith("ctoken_", StringComparison.Ordinal) ? "confirmation_token"
            : testMode && value.StartsWith("pm_", StringComparison.Ordinal) ? "payment_method" // Stripe's test payment methods only
            : null;
    }

    private static bool IsOurs(ProviderPaymentRef payment) =>
        payment.ProviderId == ProviderId && payment.Value.StartsWith("pi_", StringComparison.Ordinal) && payment.Value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static bool IsReference(string value) => value.Length is > 0 and <= PaymentReference.MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');

    private static bool IsOperationKey(string value) => value.Length is > 0 and <= OperationKey.MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':');

    private static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, Json) ?? throw new JsonException("Empty Stripe response.");

    // Nothing was sent: a definitive refusal.
    private static Result<T, ProviderError> Refused<T>(string message)
        where T : notnull =>
        Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, message));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe {Operation} failed: {ErrorKind} ({Detail})")]
    private static partial void LogFailure(ILogger logger, string operation, ProviderErrorKind errorKind, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe {Operation} returned a response the adapter cannot map ({ExceptionType})")]
    private static partial void LogUnmappable(ILogger logger, string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Count} Stripe payments carry our reference {PaymentReference}: manual review required")]
    private static partial void LogSeveralPayments(ILogger logger, string paymentReference, int count);
}
