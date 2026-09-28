using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Payments.Stripe;
using TravelBooking.Modules.Payments.Ports;
using TravelBooking.ProviderContracts.Flights;

namespace TravelBooking.ProviderContracts.Payments;

/// <summary>
/// The Stripe adapter against documentation-shaped fixtures (Stripe's API reference): the requests it sends and how it
/// reads answers, declines, errors and webhooks. No network, no real keys, no card data. The shared contract runs
/// against Stripe test mode in <see cref="StripeSandboxContractTests"/> (credential-gated).
/// </summary>
public sealed class StripePaymentProviderTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly PaymentReference _reference = new("7c9e6679742540de944be07fc1f90ae7");
    private static readonly Money _usd270 = new(270m, new CurrencyCode("USD"));

    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["Integrations:Payments:Stripe:Enabled"] = "true",
        ["Integrations:Payments:Stripe:BaseUrl"] = "https://stripe.example.test/",
        ["Integrations:Payments:Stripe:SecretKey"] = "sk_test_fixture",
        ["Integrations:Payments:Stripe:WebhookSigningSecret"] = "whsec_fixture",
        ["Integrations:Payments:Stripe:ApiVersion"] = "2026-08-26.fixture",
        ["Integrations:Payments:Stripe:Currencies:0"] = "USD",
        ["Integrations:Payments:Stripe:Currencies:1"] = "EUR",
    };

    // ---------- Authorization ----------

    [Fact]
    public async Task Authorization_creates_and_confirms_a_manual_capture_intent_keyed_by_our_reference()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("requires_capture", charge: Charge("succeeded", captured: false)));

        var payment = (await Stripe(handler).AuthorizeAsync(new AuthorizationDetails(_reference, _usd270, new PaymentMethodToken("ctoken_1abc")), Ct)).Value;

        payment.ShouldBe(new PaymentSnapshot(_reference, new ProviderPaymentRef("stripe", "pi_1"), PaymentState.Authorized, _usd270, _usd270 with { Amount = 0 }, _usd270 with { Amount = 0 }));
        var sent = handler.Requests.ShouldHaveSingleItem();
        (sent.Method, sent.Path, sent.Authorization).ShouldBe(("POST", "/v1/payment_intents", "Bearer sk_test_fixture"));
        (sent.Headers["Idempotency-Key"], sent.Headers["Stripe-Version"]).ShouldBe((_reference.Value, "2026-08-26.fixture"));
        Form(sent).ShouldBe(new Dictionary<string, string>
        {
            ["amount"] = "27000",
            ["currency"] = "usd",
            ["capture_method"] = "manual",
            ["confirm"] = "true",
            ["payment_method_types[0]"] = "card",
            ["confirmation_token"] = "ctoken_1abc",
            ["metadata[reference]"] = _reference.Value,
            ["expand[0]"] = "latest_charge",
        });
    }

    [Fact]
    public async Task A_test_mode_payment_method_is_sent_as_a_payment_method()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("requires_capture"));

        await Stripe(handler).AuthorizeAsync(new AuthorizationDetails(_reference, _usd270, new PaymentMethodToken("pm_card_visa")), Ct);

        Form(handler.Requests[0])["payment_method"].ShouldBe("pm_card_visa");
    }

    [Fact]
    public async Task A_challenge_is_RequiresAction_with_the_client_secret_and_nothing_held()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("requires_action", clientSecret: "pi_1_secret_xyz"));

        var payment = (await Stripe(handler).AuthorizeAsync(Authorization(), Ct)).Value;

        payment.State.ShouldBe(PaymentState.RequiresAction);
        payment.CustomerActionToken.ShouldBe(new CustomerActionToken("pi_1_secret_xyz"));
        payment.Captured.Amount.ShouldBe(0);
    }

    [Theory]
    [InlineData("insufficient_funds", PaymentDeclineReason.InsufficientFunds)]
    [InlineData("expired_card", PaymentDeclineReason.ExpiredCard)]
    [InlineData("do_not_honor", PaymentDeclineReason.Generic)]
    public async Task A_card_decline_is_a_Declined_payment_not_an_error(string declineCode, PaymentDeclineReason expected)
    {
        var error = $$$"""{"error":{"type":"card_error","code":"card_declined","decline_code":"{{{declineCode}}}","message":"Your card was declined.","payment_intent":{{{Intent("requires_payment_method", declineCode: declineCode)}}}}}""";
        var handler = new FakeHandler().Respond(HttpStatusCode.PaymentRequired, error).Respond(HttpStatusCode.OK, Intent("canceled", cancellationReason: "abandoned", declineCode: declineCode));

        var payment = (await Stripe(handler).AuthorizeAsync(Authorization(), Ct)).Value;

        (payment.State, payment.DeclineReason).ShouldBe((PaymentState.Declined, expected));

        // Closed first, so its client secret can never confirm it with another card later (a hold we would not track).
        (handler.Requests[1].Path, handler.Requests[1].Headers["Idempotency-Key"]).ShouldBe(("/v1/payment_intents/pi_1/cancel", $"{_reference}:close-declined"));
    }

    [Fact]
    public async Task A_decline_that_cannot_be_closed_yet_is_unknown_never_declined()
    {
        var error = $$$"""{"error":{"type":"card_error","code":"card_declined","payment_intent":{{{Intent("requires_payment_method", declineCode: "do_not_honor")}}}}}""";
        var handler = new FakeHandler().Respond(HttpStatusCode.PaymentRequired, error).Respond(HttpStatusCode.InternalServerError, """{"error":{"type":"api_error"}}""");

        (await Stripe(handler).AuthorizeAsync(Authorization(), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);
    }

    [Fact]
    public async Task Authorized_is_what_can_be_captured_so_a_partial_authorization_is_seen()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("requires_capture", capturable: 20000));

        (await Stripe(handler).AuthorizeAsync(Authorization(), Ct)).Value.Amount.ShouldBe(_usd270 with { Amount = 200m });
    }

    [Fact]
    public async Task A_payment_still_processing_is_not_mapped_and_stays_unknown()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("processing"));

        (await Stripe(handler).AuthorizeAsync(Authorization(), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable); // a write: the core treats it as unknown
    }

    [Fact]
    public async Task Voiding_an_intent_that_is_already_canceled_reads_it_and_reports_its_state()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"payment_intent_unexpected_state"}}""")
            .Respond(HttpStatusCode.OK, Intent("canceled", cancellationReason: "automatic", charge: Charge("succeeded", captured: false, refunded: 27000)));

        var voided = (await Stripe(handler).VoidAsync(new VoidDetails(_reference, new ProviderPaymentRef("stripe", "pi_1"), new OperationKey("k1:void")), Ct)).Value;

        voided.State.ShouldBe(PaymentState.Expired); // the hold had lapsed: nothing is held
        handler.Requests[1].Method.ShouldBe("GET");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"type":"api_error"}}""", ProviderErrorKind.Unknown)] // may have happened
    [InlineData(HttpStatusCode.Conflict, """{"error":{"type":"idempotency_error","code":"idempotency_key_in_use"}}""", ProviderErrorKind.OperationInProgress)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"idempotency_error"}}""", ProviderErrorKind.IdempotencyConflict)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"parameter_invalid_integer","message":"echo"}}""", ProviderErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"type":"invalid_request_error"}}""", ProviderErrorKind.AuthFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"invalid_request_error","code":"rate_limit"}}""", ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.PaymentRequired, """{"error":{"type":"card_error"}}""", ProviderErrorKind.Unknown)] // a decline without its intent: look it up
    [InlineData(HttpStatusCode.Conflict, """{"error":{"type":"invalid_request_error","code":"lock_timeout"}}""", ProviderErrorKind.Unknown)] // any other conflict proves nothing
    public async Task Stripe_errors_map_to_the_shared_taxonomy_without_copying_Stripe_messages(HttpStatusCode status, string body, ProviderErrorKind expected)
    {
        var error = (await Stripe(new FakeHandler().Respond(status, body)).AuthorizeAsync(Authorization(), Ct)).Error;

        error.Kind.ShouldBe(expected);
        error.Message.ShouldNotContain("echo");
        error.Message.ShouldNotContain("declined");
    }

    [Fact]
    public async Task An_authorization_that_times_out_is_unknown_never_a_failure()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromSeconds(5) }.Respond(HttpStatusCode.OK, Intent("requires_capture"));

        var error = (await Stripe(handler, _settings.With("Integrations:Payments:Stripe:WriteTimeout", "00:00:00.05")).AuthorizeAsync(Authorization(), Ct)).Error;

        error.Kind.ShouldBe(ProviderErrorKind.Unknown);
    }

    [Theory]
    [InlineData("270.00", "GBP", "ctoken_1")] // a currency this account is not configured for
    [InlineData("270.005", "USD", "ctoken_1")] // a fraction of a cent: never rounded
    [InlineData("270.00", "USD", "tok_visa")] // not a token this adapter sends
    [InlineData("270.00", "USD", "ctoken_1\"x")] // not a Stripe token shape
    public async Task Requests_Stripe_would_misread_are_refused_before_anything_is_sent(string amount, string currency, string token)
    {
        var handler = new FakeHandler();
        var details = new AuthorizationDetails(_reference, new Money(decimal.Parse(amount, CultureInfo.InvariantCulture), new CurrencyCode(currency)), new PaymentMethodToken(token));

        (await Stripe(handler).AuthorizeAsync(details, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);

        handler.Requests.ShouldBeEmpty();
    }

    // ---------- Capture, void, refund ----------

    [Fact]
    public async Task Capture_void_and_refund_are_keyed_by_our_operation_keys()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, Intent("succeeded", received: 25000, charge: Charge("succeeded", captured: true)))
            .Respond(HttpStatusCode.OK, Intent("canceled", cancellationReason: "abandoned", charge: Charge("succeeded", captured: false, refunded: 27000)))
            .Respond(HttpStatusCode.OK, Refund("re_1", 5000, "pending", "k1:refund"));
        var stripe = Stripe(handler);
        var payment = new ProviderPaymentRef("stripe", "pi_1");

        var captured = (await stripe.CaptureAsync(new CaptureDetails(_reference, payment, new OperationKey("k1:capture"), _usd270 with { Amount = 250m }), Ct)).Value;
        var voided = (await stripe.VoidAsync(new VoidDetails(_reference, payment, new OperationKey("k1:void")), Ct)).Value;
        var refund = (await stripe.RefundAsync(new RefundDetails(_reference, payment, new OperationKey("k1:refund"), _usd270 with { Amount = 50m }), Ct)).Value;

        (captured.State, captured.Captured.Amount).ShouldBe((PaymentState.Captured, 250m));
        (voided.State, voided.Refunded.Amount).ShouldBe((PaymentState.Voided, 0m)); // a released hold is not a refund
        refund.ShouldBe(new PaymentRefund(_reference, new OperationKey("k1:refund"), new ProviderRefundRef("stripe", "re_1"), _usd270 with { Amount = 50m }, RefundStatus.Pending));
        handler.Requests.Select(r => (r.Path, r.Headers["Idempotency-Key"])).ShouldBe(
        [
            ("/v1/payment_intents/pi_1/capture", "k1:capture"),
            ("/v1/payment_intents/pi_1/cancel", "k1:void"),
            ("/v1/refunds", "k1:refund"),
        ]);
        Form(handler.Requests[0])["amount_to_capture"].ShouldBe("25000");
        Form(handler.Requests[2]).ShouldContainKeyAndValue("metadata[operation_key]", "k1:refund");
    }

    [Fact]
    public async Task Writes_on_a_payment_that_is_not_a_Stripe_intent_are_refused_unsent()
    {
        var handler = new FakeHandler();
        var foreign = new ProviderPaymentRef("mockpay", "pi_1");

        (await Stripe(handler).VoidAsync(new VoidDetails(_reference, foreign, new OperationKey("k:void")), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await Stripe(handler).VoidAsync(new VoidDetails(_reference, new ProviderPaymentRef("stripe", "../charges"), new OperationKey("k:void")), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);

        handler.Requests.ShouldBeEmpty();
    }

    // ---------- Lookups ----------

    [Theory]
    [InlineData("canceled", "abandoned", true, PaymentState.Voided)] // we released the hold
    [InlineData("canceled", "automatic", true, PaymentState.Expired)] // the hold lapsed (F-24)
    [InlineData("canceled", "abandoned", false, PaymentState.Canceled)] // an unfinished challenge: nothing was held
    [InlineData("canceled", "expired", true, PaymentState.Expired)]
    [InlineData("requires_payment_method", null, false, PaymentState.Declined)] // e.g. failed authentication: closed, then Declined
    public async Task A_lookup_by_the_known_intent_reads_it_directly_and_maps_its_state(string status, string? reason, bool held, PaymentState expected)
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, Intent(status, cancellationReason: reason, charge: held ? Charge("succeeded", captured: false, refunded: 27000) : null))
            .Respond(HttpStatusCode.OK, Intent("canceled", cancellationReason: "abandoned"));

        var found = (await Stripe(handler).RetrieveAsync(_reference, new ProviderPaymentRef("stripe", "pi_1"), Ct)).Value.Payment.ShouldNotBeNull();

        found.State.ShouldBe(expected);
        (handler.Requests[0].Method, handler.Requests[0].Path).ShouldBe(("GET", "/v1/payment_intents/pi_1?expand[]=latest_charge"));
    }

    [Fact]
    public async Task A_decline_we_closed_is_still_reported_declined()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, Intent("canceled", cancellationReason: "abandoned", declineCode: "insufficient_funds"));

        var found = (await Stripe(handler).RetrieveAsync(_reference, new ProviderPaymentRef("stripe", "pi_1"), Ct)).Value.Payment.ShouldNotBeNull();

        (found.State, found.DeclineReason).ShouldBe((PaymentState.Declined, PaymentDeclineReason.InsufficientFunds));
        handler.Requests.ShouldHaveSingleItem(); // already closed: nothing more is sent
    }

    [Fact]
    public void Stripe_needs_an_hour_before_not_found_is_conclusive() =>
        Stripe(new FakeHandler()).MinimumNotFoundWindow.ShouldBe(TimeSpan.FromHours(1));

    [Fact]
    public async Task A_known_intent_this_key_cannot_see_is_unknown_never_absent()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.NotFound, """{"error":{"type":"invalid_request_error","code":"resource_missing"}}""");

        (await Stripe(handler).RetrieveAsync(_reference, new ProviderPaymentRef("stripe", "pi_1"), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    [Fact]
    public async Task A_lookup_by_our_reference_searches_metadata_and_reports_what_it_finds()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, $$"""{"object":"search_result","data":[{{Intent("requires_capture")}}]}""")
            .Respond(HttpStatusCode.OK, """{"object":"search_result","data":[]}""")
            .Respond(HttpStatusCode.OK, $$"""{"object":"search_result","data":[{{Intent("requires_capture")}},{{Intent("requires_capture")}}]}""")
            .Respond(HttpStatusCode.OK, $$"""{"object":"search_result","data":[{{Intent("requires_capture", reference: "someone-else")}}]}""");
        var stripe = Stripe(handler);

        (await stripe.RetrieveAsync(_reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Authorized);
        (await stripe.RetrieveAsync(_reference, Ct)).Value.Found.ShouldBeFalse(); // conclusive only after the core's window
        (await stripe.RetrieveAsync(_reference, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable); // two payments: a person decides
        (await stripe.RetrieveAsync(_reference, Ct)).Value.Payment.ShouldNotBeNull().Reference.ShouldNotBe(_reference); // the core sees a mismatch

        Uri.UnescapeDataString(handler.Requests[0].Path).ShouldBe($"/v1/payment_intents/search?query=metadata['reference']:'{_reference}'&expand[]=data.latest_charge");
    }

    [Fact]
    public async Task A_refund_is_found_by_our_key_among_the_payment_refunds()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.OK, $$"""{"data":[{{Intent("succeeded", received: 27000, charge: Charge("succeeded", captured: true, refunded: 5000))}}]}""")
            .Respond(HttpStatusCode.OK, $$"""{"object":"list","data":[{{Refund("re_0", 1000, "succeeded", "other:refund")}},{{Refund("re_1", 5000, "succeeded", "k1:refund")}}]}""");

        var refund = (await Stripe(handler).RetrieveRefundAsync(_reference, new OperationKey("k1:refund"), Ct)).Value.Refund.ShouldNotBeNull();

        (refund.Refund.Value, refund.Status).ShouldBe(("re_1", RefundStatus.Succeeded));
        handler.Requests[1].Path.ShouldBe("/v1/refunds?payment_intent=pi_1&limit=100");
    }

    // ---------- Configuration ----------

    [Theory]
    [InlineData("Integrations:Payments:Stripe:SecretKey", "sk_live_x")] // never a live key while not production-ready
    [InlineData("Integrations:Payments:Stripe:WebhookSigningSecret", "secret")]
    [InlineData("Integrations:Payments:Stripe:Currencies:0", "TND")] // three decimals: needs Stripe's confirmation first
    [InlineData("Integrations:Payments:Stripe:BaseUrl", "http://stripe.example.test/")]
    [InlineData("Integrations:Payments:Stripe:WebhookTolerance", "00:00:00")]
    public void Unsafe_settings_fail_at_startup(string key, string value) =>
        Should.Throw<OptionsValidationException>(() => Stripe(new FakeHandler(), _settings.With(key, value)));

    [Fact]
    public void The_adapter_is_not_production_ready() =>
        Stripe(new FakeHandler()).IsProductionReady.ShouldBeFalse();

    // ---------- Notifications (webhooks) ----------

    [Fact]
    public void A_signed_payment_event_is_read_for_its_id_intent_and_our_reference_only()
    {
        var body = Event("evt_1", "payment_intent.amount_capturable_updated", Intent("requires_capture"));

        var notification = Notifications().Verify(body, Signed(body, _now)).Value;

        notification.ShouldBe(new PaymentNotification("evt_1", PaymentNotificationKind.Payment, _reference, new ProviderPaymentRef("stripe", "pi_1"), "payment_intent.amount_capturable_updated", _now));
    }

    [Fact]
    public void A_refund_event_names_its_payment_intent()
    {
        var body = Event("evt_2", "refund.updated", Refund("re_1", 5000, "succeeded", "k1:refund"));

        var notification = Notifications().Verify(body, Signed(body, _now)).Value;

        (notification.Kind, notification.Payment).ShouldBe((PaymentNotificationKind.Refund, new ProviderPaymentRef("stripe", "pi_1")));
    }

    [Fact]
    public void Events_the_core_does_not_act_on_are_ignored()
    {
        var body = Event("evt_3", "customer.created", """{"id":"cus_1","object":"customer"}""");

        Notifications().Verify(body, Signed(body, _now)).Value.Kind.ShouldBe(PaymentNotificationKind.Ignored);
    }

    [Fact]
    public void Unsigned_forged_tampered_or_downgraded_events_are_rejected()
    {
        var body = Event("evt_1", "payment_intent.succeeded", Intent("succeeded"));
        var notifications = Notifications();
        var timestamp = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        notifications.Verify(body, new Dictionary<string, string>()).Error.ShouldBe(PaymentNotificationRejection.InvalidSignature);
        notifications.Verify(body, Signed(body, _now, secret: "whsec_other")).Error.ShouldBe(PaymentNotificationRejection.InvalidSignature);
        notifications.Verify(body.Replace("evt_1", "evt_9", StringComparison.Ordinal), Signed(body, _now)).Error.ShouldBe(PaymentNotificationRejection.InvalidSignature);
        notifications.Verify(body, Header($"t={timestamp},v0={Sign(body, _now, "whsec_fixture")}")).Error.ShouldBe(PaymentNotificationRejection.InvalidSignature);
    }

    [Fact]
    public void One_valid_signature_among_several_is_enough_while_a_secret_is_rolled()
    {
        var body = Event("evt_1", "payment_intent.succeeded", Intent("succeeded"));
        var timestamp = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var header = Header($"t={timestamp},v1={Sign(body, _now, "whsec_old")},v1={Sign(body, _now, "whsec_fixture")}");

        Notifications().Verify(body, header).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-6)] // older than the tolerance: a possible replay
    [InlineData(6)] // from the future
    public void Signed_events_outside_the_tolerance_are_stale(int minutes)
    {
        var body = Event("evt_1", "payment_intent.succeeded", Intent("succeeded"));

        Notifications().Verify(body, Signed(body, _now.AddMinutes(minutes))).Error.ShouldBe(PaymentNotificationRejection.Stale);
    }

    [Fact]
    public void Live_mode_events_and_unreadable_bodies_are_malformed_for_a_test_mode_key()
    {
        var live = Event("evt_1", "payment_intent.succeeded", Intent("succeeded"), livemode: true);

        Notifications().Verify(live, Signed(live, _now)).Error.ShouldBe(PaymentNotificationRejection.Malformed);
        Notifications().Verify("not json", Signed("not json", _now)).Error.ShouldBe(PaymentNotificationRejection.Malformed);
    }

    // ---------- Helpers ----------

    private static IPaymentProvider Stripe(FakeHandler handler, Dictionary<string, string?>? settings = null) =>
        Compose(handler, settings).GetRequiredService<IPaymentProvider>();

    private static IPaymentNotifications Notifications() => Compose(new FakeHandler(), null).GetRequiredService<IPaymentNotifications>();

    private static ServiceProvider Compose(FakeHandler handler, Dictionary<string, string?>? settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? _settings).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
        services.AddStripePaymentProvider(configuration);
        services.AddHttpClient(StripePaymentProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<StripeOptions>>().Value; // startup validation (ValidateOnStart runs in a host)
        return provider;
    }

    private static AuthorizationDetails Authorization() => new(_reference, _usd270, new PaymentMethodToken("ctoken_1abc"));

    private static string Intent(
        string status, string? clientSecret = null, string? cancellationReason = null, string? declineCode = null, long received = 0, string? charge = null, string? reference = null, long? capturable = null) =>
        $$"""
        {"id":"pi_1","object":"payment_intent","amount":27000,"amount_capturable":{{capturable ?? (status == "requires_capture" ? 27000 : 0)}},"amount_received":{{received}},"currency":"usd","status":"{{status}}",
         "metadata":{"reference":"{{reference ?? _reference.Value}}"},"client_secret":{{Json(clientSecret)}},"cancellation_reason":{{Json(cancellationReason)}},
         "last_payment_error":{{(declineCode is null ? "null" : $$"""{"type":"card_error","code":"card_declined","decline_code":"{{declineCode}}"}""")}},
         "latest_charge":{{charge ?? "null"}},"livemode":false}
        """;

    private static string Charge(string status, bool captured, long refunded = 0) =>
        $$"""{"id":"ch_1","object":"charge","status":"{{status}}","captured":{{(captured ? "true" : "false")}},"amount_refunded":{{refunded}}}""";

    private static string Refund(string id, long amount, string status, string key) =>
        $$$"""{"id":"{{{id}}}","object":"refund","amount":{{{amount}}},"currency":"usd","status":"{{{status}}}","payment_intent":"pi_1","metadata":{"reference":"{{{_reference}}}","operation_key":"{{{key}}}"}}""";

    private static string Event(string id, string type, string dataObject, bool livemode = false) =>
        $$$"""{"id":"{{{id}}}","object":"event","type":"{{{type}}}","livemode":{{{(livemode ? "true" : "false")}}},"created":{{{_now.ToUnixTimeSeconds()}}},"data":{"object":{{{dataObject}}}}}""";

    private static string Json(string? value) => value is null ? "null" : $"\"{value}\"";

    private static Dictionary<string, string> Signed(string body, DateTimeOffset at, string secret = "whsec_fixture") =>
        Header($"t={at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)},v1={Sign(body, at, secret)}");

    private static Dictionary<string, string> Header(string value) => new(StringComparer.OrdinalIgnoreCase) { ["Stripe-Signature"] = value };

    private static string Sign(string body, DateTimeOffset at, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{body}")));

    private static Dictionary<string, string> Form(SentRequest request) =>
        request.Body!.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>
/// The shared payment contract against Stripe TEST mode, credential-gated: without STRIPE_TEST_SECRET_KEY and
/// STRIPE_TEST_API_VERSION every test is reported as skipped, never as passed. Uses Stripe's test payment methods only,
/// never card numbers. The adapter may be marked production-ready only after this passes (ADR 0006).
/// </summary>
public sealed class StripeSandboxContractTests : PaymentProviderContract
{
    private readonly Lazy<IPaymentProvider?> _provider = new(Create);

    protected override IPaymentProvider Provider => _provider.Value ?? Skip();

    protected override string Currency => "USD";

    // Stripe's search can lag its writes, normally by under a minute.
    protected override TimeSpan ReferenceLookupLag => TimeSpan.FromSeconds(90);

    // Stripe's test payment methods (to confirm in the sandbox run): approved, generic decline, and 3DS required.
    protected override PaymentMethodToken ApprovedMethod => new("pm_card_visa");

    protected override PaymentMethodToken DeclinedMethod => new("pm_card_visa_chargeDeclined");

    protected override PaymentMethodToken ChallengeMethod => new("pm_card_authenticationRequired");

    private static IPaymentProvider? Create()
    {
        var key = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY");
        var version = Environment.GetEnvironmentVariable("STRIPE_TEST_API_VERSION");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:Payments:Stripe:Enabled"] = "true",
            ["Integrations:Payments:Stripe:BaseUrl"] = "https://api.stripe.com/",
            ["Integrations:Payments:Stripe:SecretKey"] = key,
            ["Integrations:Payments:Stripe:WebhookSigningSecret"] = "whsec_unused_by_the_contract",
            ["Integrations:Payments:Stripe:ApiVersion"] = version,
            ["Integrations:Payments:Stripe:Currencies:0"] = "USD",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddStripePaymentProvider(configuration).BuildServiceProvider();
        return services.GetRequiredService<IPaymentProvider>();
    }

    private static IPaymentProvider Skip()
    {
        Assert.Skip("Stripe test-mode credentials are not configured (STRIPE_TEST_SECRET_KEY, STRIPE_TEST_API_VERSION).");
        return null!;
    }
}
