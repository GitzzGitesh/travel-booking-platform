using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.ProviderContracts.Payments;

/// <summary>
/// Semantics every <see cref="IPaymentProvider"/> must honour, the mock included (ADR 0004: mock parity). A real
/// adapter runs this against its provider's test mode with the provider's own test payment methods (never real cards).
/// </summary>
public abstract class PaymentProviderContract
{
    protected abstract IPaymentProvider Provider { get; }

    /// <summary>A test payment method the provider always authorizes.</summary>
    protected abstract PaymentMethodToken ApprovedMethod { get; }

    /// <summary>A test payment method the provider always declines.</summary>
    protected abstract PaymentMethodToken DeclinedMethod { get; }

    /// <summary>A test payment method that needs a customer challenge (SCA).</summary>
    protected abstract PaymentMethodToken ChallengeMethod { get; }

    protected static Money Amount(decimal amount, string currency = "XTS") => new(amount, new CurrencyCode(currency));

    // Losing a race on one key is either the same result or an unknown outcome to look up; never a definitive refusal.
    private static readonly ProviderErrorKind[] _unknownKinds = [ProviderErrorKind.Unknown, ProviderErrorKind.OperationInProgress];

    [Fact]
    public async Task An_authorization_holds_the_amount_and_is_found_by_our_reference()
    {
        var details = Authorization(270m);

        var payment = (await Provider.AuthorizeAsync(details, Ct)).Value;
        var lookup = (await Provider.RetrieveAsync(details.Reference, Ct)).Value;

        payment.State.ShouldBe(PaymentState.Authorized);
        payment.Reference.ShouldBe(details.Reference);
        payment.Payment.ProviderId.ShouldBe(Provider.Id);
        payment.Amount.ShouldBe(details.Amount);
        payment.Captured.Amount.ShouldBe(0);
        lookup.Payment.ShouldNotBeNull().Payment.ShouldBe(payment.Payment);
    }

    [Fact]
    public async Task Authorizing_again_with_the_same_reference_never_creates_a_second_payment()
    {
        var details = Authorization(270m);

        var first = (await Provider.AuthorizeAsync(details, Ct)).Value;
        var again = (await Provider.AuthorizeAsync(details, Ct)).Value;

        again.Payment.ShouldBe(first.Payment);
    }

    [Fact]
    public async Task Parallel_authorizations_with_one_reference_create_one_payment_and_never_a_definitive_refusal()
    {
        var details = Authorization(270m);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider.AuthorizeAsync(details, Ct)));

        results.Where(r => r.IsSuccess).Select(r => r.Value.Payment).Distinct().Count().ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => _unknownKinds.Contains(r.Error.Kind));
    }

    [Fact]
    public async Task The_same_reference_with_another_amount_is_an_idempotency_conflict()
    {
        var details = Authorization(270m);
        await Provider.AuthorizeAsync(details, Ct);

        var conflict = await Provider.AuthorizeAsync(details with { Amount = Amount(300m) }, Ct);

        conflict.Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);
    }

    [Fact]
    public async Task A_declined_payment_holds_nothing_and_a_new_attempt_with_a_new_reference_can_succeed()
    {
        var declined = (await Provider.AuthorizeAsync(Authorization(270m, DeclinedMethod), Ct)).Value;
        var retry = (await Provider.AuthorizeAsync(Authorization(270m), Ct)).Value;

        declined.State.ShouldBe(PaymentState.Declined);
        declined.Captured.Amount.ShouldBe(0);
        retry.State.ShouldBe(PaymentState.Authorized);
    }

    [Fact]
    public async Task A_challenge_holds_nothing_until_completed_and_voiding_it_cancels_it()
    {
        var payment = await Authorized(270m, ChallengeMethod);

        payment.State.ShouldBe(PaymentState.RequiresAction);
        payment.CustomerActionToken.ShouldNotBeNull();
        (await Provider.CaptureAsync(Capture(payment, payment.Amount), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await Provider.VoidAsync(new VoidDetails(payment.Reference, payment.Payment, NewKey("void")), Ct)).Value.State.ShouldBe(PaymentState.Canceled);
    }

    [Fact]
    public async Task A_capture_takes_up_to_the_authorized_amount_and_repeats_safely_with_its_key()
    {
        var payment = await Authorized(270m);
        var capture = Capture(payment, Amount(250m));

        var captured = (await Provider.CaptureAsync(capture, Ct)).Value;
        var replay = (await Provider.CaptureAsync(capture, Ct)).Value;
        var lookup = (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!;

        captured.State.ShouldBe(PaymentState.Captured);
        captured.Captured.ShouldBe(Amount(250m)); // partial capture: a partially confirmed order
        replay.Captured.ShouldBe(Amount(250m)); // never a second capture
        lookup.Captured.ShouldBe(Amount(250m));
    }

    [Fact]
    public async Task There_is_one_capture_per_payment_a_second_under_another_key_is_refused()
    {
        var payment = await Authorized(270m);
        await Provider.CaptureAsync(Capture(payment, Amount(100m)), Ct);

        var second = await Provider.CaptureAsync(Capture(payment, Amount(100m)), Ct);

        second.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!.Captured.ShouldBe(Amount(100m));
    }

    [Theory]
    [InlineData(270.01, "XTS")] // above the authorized amount
    [InlineData(100, "XXX")] // another currency
    public async Task A_capture_outside_the_authorization_is_refused(decimal amount, string currency)
    {
        var payment = await Authorized(270m);

        var result = await Provider.CaptureAsync(Capture(payment, Amount(amount, currency)), Ct);

        result.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Parallel_captures_with_one_key_capture_once()
    {
        var payment = await Authorized(270m);
        var capture = Capture(payment, payment.Amount);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider.CaptureAsync(capture, Ct)));

        results.Where(r => r.IsSuccess).ShouldAllBe(r => r.Value.Captured == payment.Amount);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => _unknownKinds.Contains(r.Error.Kind));
        (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!.Captured.ShouldBe(payment.Amount);
    }

    [Fact]
    public async Task A_void_releases_an_uncaptured_hold_repeats_safely_and_shows_in_the_lookup()
    {
        var payment = await Authorized(270m);
        var voiding = new VoidDetails(payment.Reference, payment.Payment, NewKey("void"));

        (await Provider.VoidAsync(voiding, Ct)).Value.State.ShouldBe(PaymentState.Voided);
        (await Provider.VoidAsync(voiding, Ct)).Value.State.ShouldBe(PaymentState.Voided);

        (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!.State.ShouldBe(PaymentState.Voided);
        (await Provider.CaptureAsync(Capture(payment, Amount(1m)), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task A_captured_payment_cannot_be_voided()
    {
        var payment = await Captured(270m);

        var result = await Provider.VoidAsync(new VoidDetails(payment.Reference, payment.Payment, NewKey("void")), Ct);

        result.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Refunds_are_records_found_by_our_key_never_exceeding_the_captured_amount()
    {
        var payment = await Captured(270m);
        var first = new RefundDetails(payment.Reference, payment.Payment, NewKey("refund"), Amount(100m));

        var refund = (await Provider.RefundAsync(first, Ct)).Value;
        var replay = (await Provider.RefundAsync(first, Ct)).Value;
        var found = (await Provider.RetrieveRefundAsync(payment.Reference, first.Key, Ct)).Value.Refund;

        refund.Key.ShouldBe(first.Key);
        refund.Amount.ShouldBe(Amount(100m));
        refund.Status.ShouldNotBe(RefundStatus.Failed);
        replay.Refund.ShouldBe(refund.Refund); // same key: the same refund, never a second one
        found.ShouldNotBeNull().Refund.ShouldBe(refund.Refund);
        (await Provider.RefundAsync(first with { Key = NewKey("refund"), Amount = Amount(170.01m) }, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await Provider.RefundAsync(first with { Key = NewKey("refund"), Amount = Amount(170m) }, Ct)).Value.Amount.ShouldBe(Amount(170m));
        (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!.Refunded.ShouldBe(Amount(270m));
    }

    [Fact]
    public async Task Parallel_refunds_with_one_key_refund_once()
    {
        var payment = await Captured(270m);
        var refund = new RefundDetails(payment.Reference, payment.Payment, NewKey("refund"), Amount(50m));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider.RefundAsync(refund, Ct)));

        results.Where(r => r.IsSuccess).Select(r => r.Value.Refund).Distinct().Count().ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => _unknownKinds.Contains(r.Error.Kind));
        (await Provider.RetrieveAsync(payment.Reference, Ct)).Value.Payment!.Refunded.ShouldBe(Amount(50m));
    }

    [Fact]
    public async Task Uncaptured_or_voided_payments_cannot_be_refunded()
    {
        var authorized = await Authorized(270m);
        var voided = await Authorized(270m);
        await Provider.VoidAsync(new VoidDetails(voided.Reference, voided.Payment, NewKey("void")), Ct);

        (await Provider.RefundAsync(new RefundDetails(authorized.Reference, authorized.Payment, NewKey("refund"), Amount(10m)), Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await Provider.RefundAsync(new RefundDetails(voided.Reference, voided.Payment, NewKey("refund"), Amount(10m)), Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task A_key_reused_for_another_operation_is_an_idempotency_conflict()
    {
        var payment = await Captured(270m);
        var key = NewKey("refund");
        await Provider.RefundAsync(new RefundDetails(payment.Reference, payment.Payment, key, Amount(10m)), Ct);

        (await Provider.RefundAsync(new RefundDetails(payment.Reference, payment.Payment, key, Amount(20m)), Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);

        var other = await Authorized(270m);
        var voidKey = NewKey("void");
        await Provider.VoidAsync(new VoidDetails(other.Reference, other.Payment, voidKey), Ct);
        (await Provider.CaptureAsync(new CaptureDetails(other.Reference, other.Payment, voidKey, Amount(1m)), Ct))
            .Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);
    }

    [Fact]
    public async Task Looking_up_unknown_references_and_keys_is_a_definite_not_found()
    {
        (await Provider.RetrieveAsync(NewReference(), Ct)).Value.Found.ShouldBeFalse();
        (await Provider.RetrieveRefundAsync(NewReference(), NewKey("refund"), Ct)).Value.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_sending()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider.AuthorizeAsync(Authorization(1m), cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Provider.RetrieveAsync(NewReference(), cancelled.Token));
    }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected AuthorizationDetails Authorization(decimal amount, PaymentMethodToken? method = null) =>
        new(NewReference(), Amount(amount), method ?? ApprovedMethod);

    protected async Task<PaymentSnapshot> Authorized(decimal amount, PaymentMethodToken? method = null) =>
        (await Provider.AuthorizeAsync(Authorization(amount, method), Ct)).Value;

    protected async Task<PaymentSnapshot> Captured(decimal amount, PaymentMethodToken? method = null)
    {
        var payment = await Authorized(amount, method);
        return (await Provider.CaptureAsync(Capture(payment, payment.Amount), Ct)).Value;
    }

    // A fresh key per call: one capture per payment means a second call is a second, refused, capture.
    protected static CaptureDetails Capture(PaymentSnapshot payment, Money amount) =>
        new(payment.Reference, payment.Payment, NewKey("capture"), amount);

    protected static PaymentReference NewReference() => new($"pay-{Guid.NewGuid():N}");

    protected static OperationKey NewKey(string operation) => new($"{Guid.NewGuid():N}:{operation}");
}
