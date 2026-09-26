using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>
/// Payment outcomes (payment-lifecycle.md, booking rules): authorized, declined, unknown, captured, voided and refunds
/// stay distinct; one provider write per call; results are checked against what was asked.
/// </summary>
public sealed class PaymentOperationsTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly PaymentReference _reference = new("pay-42");
    private static readonly ProviderPaymentRef _providerRef = new("stub", "pay_1");
    private static readonly Money _amount = Xts(270m);
    private static readonly AuthorizationDetails _authorize = new(_reference, _amount, new PaymentMethodToken("pm_test"));
    private static readonly CaptureDetails _capture = new(_reference, _providerRef, new OperationKey("pay-42:capture"), _amount);
    private static readonly VoidDetails _void = new(_reference, _providerRef, new OperationKey("pay-42:void"));
    private static readonly RefundDetails _refund = new(_reference, _providerRef, new OperationKey("refund-1:refund"), Xts(50m));

    [Theory]
    [InlineData(PaymentState.Authorized, typeof(PaymentOutcome.Authorized))]
    [InlineData(PaymentState.RequiresAction, typeof(PaymentOutcome.ActionRequired))]
    [InlineData(PaymentState.Declined, typeof(PaymentOutcome.Declined))]
    [InlineData(PaymentState.Canceled, typeof(PaymentOutcome.Canceled))]
    [InlineData(PaymentState.Expired, typeof(PaymentOutcome.AuthorizationExpired))]
    [InlineData(PaymentState.Captured, typeof(PaymentOutcome.Captured))] // a replayed authorization after capture
    [InlineData(PaymentState.Voided, typeof(PaymentOutcome.Voided))]
    public async Task An_authorization_is_classified_by_the_payment_state(PaymentState state, Type expected)
    {
        var outcome = await Operations(Returns(Snapshot(state, captured: state is PaymentState.Captured ? 270m : 0m))).AuthorizeAsync(_authorize, Ct);

        outcome.ShouldBeOfType(expected);
    }

    [Fact]
    public async Task A_decline_is_definitive_with_its_reason()
    {
        var outcome = await Operations(Returns(Snapshot(PaymentState.Declined) with { DeclineReason = PaymentDeclineReason.InsufficientFunds })).AuthorizeAsync(_authorize, Ct);

        outcome.ShouldBe(new PaymentOutcome.Declined(PaymentDeclineReason.InsufficientFunds));
    }

    [Theory]
    [InlineData(ProviderErrorKind.InvalidRequest)]
    [InlineData(ProviderErrorKind.IdempotencyConflict)]
    public void Refusals_of_the_request_itself_are_Rejected(ProviderErrorKind kind) =>
        PaymentOperations.Classify(new ProviderError(kind, "stub")).ShouldBe(new PaymentOutcome.Rejected(kind));

    [Fact]
    public void Only_the_request_refusals_are_definitive()
    {
        foreach (var kind in Enum.GetValues<ProviderErrorKind>())
        {
            var definitive = kind is ProviderErrorKind.InvalidRequest or ProviderErrorKind.IdempotencyConflict;
            (PaymentOperations.Classify(new ProviderError(kind, "stub")) is PaymentOutcome.Unknown).ShouldBe(!definitive, kind.ToString());
        }
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unknown)]
    [InlineData(ProviderErrorKind.OperationInProgress)] // the same key still being processed: not a refusal
    [InlineData(ProviderErrorKind.Unavailable)]
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.AuthFailure)]
    public async Task Ambiguous_write_failures_are_Unknown_logged_without_payment_data_and_not_repeated(ProviderErrorKind kind)
    {
        var provider = new StubProvider { Payment = Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(kind, "stub")) };
        var logger = new RecordingLogger();

        var outcome = await new PaymentOperations(provider, new FakeTimeProvider(_now), logger).AuthorizeAsync(_authorize, Ct);

        outcome.ShouldBe(new PaymentOutcome.Unknown(kind));
        provider.Writes.ShouldBe(1);
        var message = logger.Messages.ShouldHaveSingleItem();
        message.ShouldContain("pay-42");
        message.ShouldNotContain("pm_test");
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task An_exception_from_a_started_write_is_Unknown_never_a_failure(Type exception)
    {
        var provider = new StubProvider { Throw = (Exception)Activator.CreateInstance(exception)! };

        var outcome = await Operations(provider).CaptureAsync(_capture, Ct);

        outcome.ShouldBeOfType<PaymentOutcome.Unknown>();
        provider.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelled_before_sending_attempts_nothing()
    {
        var provider = Returns(Snapshot(PaymentState.Authorized));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Operations(provider).AuthorizeAsync(_authorize, cancelled.Token));
        provider.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Captured_voided_and_cancelled_are_distinct_outcomes()
    {
        (await Operations(Returns(Snapshot(PaymentState.Captured, captured: 270m))).CaptureAsync(_capture, Ct)).ShouldBeOfType<PaymentOutcome.Captured>();
        (await Operations(Returns(Snapshot(PaymentState.Voided))).VoidAsync(_void, Ct)).ShouldBeOfType<PaymentOutcome.Voided>();
        (await Operations(Returns(Snapshot(PaymentState.Canceled))).VoidAsync(_void, Ct)).ShouldBeOfType<PaymentOutcome.Canceled>();
    }

    [Theory]
    [InlineData(RefundStatus.Succeeded, typeof(PaymentOutcome.RefundSucceeded))]
    [InlineData(RefundStatus.Pending, typeof(PaymentOutcome.RefundPending))]
    [InlineData(RefundStatus.Failed, typeof(PaymentOutcome.RefundFailed))]
    public async Task A_refund_is_judged_by_its_own_record_and_status(RefundStatus status, Type expected)
    {
        var outcome = await Operations(ReturnsRefund(Refund(status))).RefundAsync(_refund, Ct);

        outcome.ShouldBeOfType(expected);
    }

    [Theory]
    [MemberData(nameof(RefundsNotAsRequested))]
    public async Task A_refund_answer_for_another_refund_or_amount_is_a_Mismatch(string _, PaymentRefund answer)
    {
        var outcome = await Operations(ReturnsRefund(answer)).RefundAsync(_refund, Ct);

        outcome.ShouldBeOfType<PaymentOutcome.Mismatch>().Refund.ShouldBe(answer);
    }

    public static TheoryData<string, PaymentRefund> RefundsNotAsRequested() => new()
    {
        { "an earlier refund of the payment (stale answer)", Refund(RefundStatus.Succeeded) with { Key = new OperationKey("refund-0:refund"), Amount = Xts(100m) } },
        { "another amount", Refund(RefundStatus.Succeeded) with { Amount = Xts(49m) } },
        { "another currency", Refund(RefundStatus.Succeeded) with { Amount = new Money(50m, new CurrencyCode("EUR")) } },
        { "another provider", Refund(RefundStatus.Succeeded) with { Refund = new ProviderRefundRef("other", "re_1") } },
    };

    [Theory]
    [MemberData(nameof(PaymentsNotAsRequested))]
    public async Task A_provider_answer_other_than_requested_is_a_Mismatch(string _, PaymentSnapshot answer, int operation)
    {
        var operations = Operations(Returns(answer));

        var outcome = operation switch
        {
            0 => await operations.AuthorizeAsync(_authorize, Ct),
            1 => await operations.CaptureAsync(_capture, Ct),
            _ => await operations.VoidAsync(_void, Ct),
        };

        outcome.ShouldBeOfType<PaymentOutcome.Mismatch>();
    }

    public static TheoryData<string, PaymentSnapshot, int> PaymentsNotAsRequested() => new()
    {
        { "authorized another amount", Snapshot(PaymentState.Authorized) with { Amount = Xts(1m) }, 0 },
        { "another reference", Snapshot(PaymentState.Authorized) with { Reference = new PaymentReference("someone-else") }, 0 },
        { "another provider", Snapshot(PaymentState.Authorized) with { Payment = new ProviderPaymentRef("other", "pay_1") }, 0 },
        { "captured another amount", Snapshot(PaymentState.Captured, captured: 100m), 1 },
        { "capture reported as not captured", Snapshot(PaymentState.Authorized), 1 },
        { "void reported as captured", Snapshot(PaymentState.Captured, captured: 270m), 2 },
    };

    [Fact]
    public async Task Reconciliation_reports_the_provider_state_or_not_found_as_of_now()
    {
        var found = new StubProvider { Lookup = Result<PaymentLookup, ProviderError>.Success(new PaymentLookup(Snapshot(PaymentState.Expired))) };
        var missing = new StubProvider { Lookup = Result<PaymentLookup, ProviderError>.Success(new PaymentLookup(null)) };
        var failing = new StubProvider { Lookup = Result<PaymentLookup, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "stub")) };
        var throwing = new StubProvider { Throw = new HttpRequestException() };

        (await Operations(found).ReconcileAsync(_reference, Ct)).ShouldBeOfType<PaymentOutcome.AuthorizationExpired>();
        (await Operations(missing).ReconcileAsync(_reference, Ct)).ShouldBe(new PaymentOutcome.NotFound(_now));
        (await Operations(failing).ReconcileAsync(_reference, Ct)).ShouldBe(new PaymentOutcome.Unknown(ProviderErrorKind.Unavailable));
        (await Operations(throwing).ReconcileAsync(_reference, Ct)).ShouldBeOfType<PaymentOutcome.Unknown>();
        found.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task A_refund_is_reconciled_by_our_key()
    {
        var provider = new StubProvider { RefundLookup = Result<RefundLookup, ProviderError>.Success(new RefundLookup(Refund(RefundStatus.Succeeded))) };

        (await Operations(provider).ReconcileRefundAsync(_reference, _refund.Key, _refund.Amount, Ct)).ShouldBeOfType<PaymentOutcome.RefundSucceeded>();
        (await Operations(provider).ReconcileRefundAsync(_reference, _refund.Key, Xts(1m), Ct)).ShouldBeOfType<PaymentOutcome.Mismatch>();
    }

    [Theory]
    [InlineData("4242424242424242")]
    [InlineData("4242 4242 4242 4242")]
    [InlineData("4242-4242-4242-4242")]
    [InlineData("4242.4242.4242.4242")]
    [InlineData("378282246310005")]
    [InlineData("4242424242424242|12/30|123")]
    [InlineData("card:4242424242424242")]
    public void A_card_number_is_never_accepted_as_a_payment_method_token(string value)
    {
        var exception = Should.Throw<ArgumentException>(() => new PaymentMethodToken(value));

        exception.Message.ShouldNotContain(value);
    }

    [Theory]
    [InlineData("pm_1Nabc2DEF3ghi4JKL")]
    [InlineData("1234567890123456")] // an all-digit provider token that fails the Luhn check
    public void Provider_tokens_are_accepted(string value) =>
        new PaymentMethodToken(value).Value.ShouldBe(value);

    [Fact]
    public void Tokens_never_print_themselves()
    {
        new PaymentMethodToken("pm_secretish").ToString().ShouldNotContain("pm_secretish");
        (Snapshot(PaymentState.RequiresAction) with { CustomerActionToken = new CustomerActionToken("secret_abc") }).ToString().ShouldNotContain("secret_abc");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    public void References_and_keys_are_short_ascii_identifiers(string value)
    {
        Should.Throw<ArgumentException>(() => new PaymentReference(value));
        Should.Throw<ArgumentException>(() => new OperationKey(value));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Money Xts(decimal amount) => new(amount, new CurrencyCode("XTS"));

    private static PaymentSnapshot Snapshot(PaymentState state, decimal captured = 0m, decimal refunded = 0m) =>
        new(_reference, _providerRef, state, _amount, Xts(captured), Xts(refunded));

    private static PaymentRefund Refund(RefundStatus status) =>
        new(_reference, _refund.Key, new ProviderRefundRef("stub", "re_1"), _refund.Amount, status);

    private static StubProvider Returns(PaymentSnapshot snapshot) => new() { Payment = Result<PaymentSnapshot, ProviderError>.Success(snapshot) };

    private static StubProvider ReturnsRefund(PaymentRefund refund) => new() { RefundResult = Result<PaymentRefund, ProviderError>.Success(refund) };

    private static PaymentOperations Operations(IPaymentProvider provider) => new(provider, new FakeTimeProvider(_now), new RecordingLogger());

    private sealed class StubProvider : IPaymentProvider
    {
        public Result<PaymentSnapshot, ProviderError>? Payment { get; init; }

        public Result<PaymentRefund, ProviderError>? RefundResult { get; init; }

        public Result<PaymentLookup, ProviderError>? Lookup { get; init; }

        public Result<RefundLookup, ProviderError>? RefundLookup { get; init; }

        public Exception? Throw { get; init; }

        public int Writes { get; private set; }

        public string Id => "stub";

        public Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken) => Write(Payment);

        public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) => Write(Payment);

        public Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken) => Write(Payment);

        public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) => Write(RefundResult);

        public Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken) =>
            Throw is null ? Task.FromResult(Lookup!) : Task.FromException<Result<PaymentLookup, ProviderError>>(Throw);

        public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) =>
            Task.FromResult(RefundLookup!);

        private Task<Result<T, ProviderError>> Write<T>(Result<T, ProviderError>? result)
            where T : notnull
        {
            Writes++;
            return Throw is null ? Task.FromResult(result!) : Task.FromException<Result<T, ProviderError>>(Throw);
        }
    }

    private sealed class RecordingLogger : ILogger<PaymentOperations>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
