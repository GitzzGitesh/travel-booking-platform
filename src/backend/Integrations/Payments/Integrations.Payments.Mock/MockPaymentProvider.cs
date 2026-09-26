using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock;

/// <summary>
/// A deterministic payment provider: no network, no randomness, no card data. The outcome is chosen by the payment
/// method token (<see cref="MockPaymentMethods"/>), so one running Api can show every scenario. Payments are held in
/// memory for the life of the process (Development and Staging only). Idempotent by our keys, as a real provider is.
/// </summary>
internal sealed class MockPaymentProvider(IOptions<MockPaymentProviderOptions> options) : IPaymentProvider
{
    public const string ProviderId = "mockpay";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, MockPayment> _payments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _operations = new(StringComparer.Ordinal); // key -> what it did
    private readonly Dictionary<string, PaymentRefund> _refunds = new(StringComparer.Ordinal); // key -> refund
    private readonly HashSet<string> _failedOnce = new(StringComparer.Ordinal);

    public string Id => ProviderId;

    public Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Authorize(details));

    public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Capture(details));

    public Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Void(details));

    public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Refund(details));

    public Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Result<PaymentLookup, ProviderError>.Success(
            new PaymentLookup(_payments.TryGetValue(reference.Value, out var payment) ? payment.Snapshot() : null)));

    public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) =>
        Run(cancellationToken, () => Result<RefundLookup, ProviderError>.Success(
            new RefundLookup(_refunds.TryGetValue(key.Value, out var refund) && refund.Payment == reference ? refund : null)));

    private Result<PaymentSnapshot, ProviderError> Authorize(AuthorizationDetails details)
    {
        var fingerprint = $"{details.Amount}|{details.PaymentMethod.Value}";
        if (_payments.TryGetValue(details.Reference.Value, out var existing))
        {
            return existing.Fingerprint == fingerprint
                ? Success(existing)
                : Failure(ProviderErrorKind.IdempotencyConflict, "The payment reference was already used with other details.");
        }

        var method = details.PaymentMethod.Value;
        if (details.Amount.Amount <= 0 || !MockPaymentMethods.All.Contains(method))
        {
            return Failure(ProviderErrorKind.InvalidRequest, "The amount must be positive and the mock payment method recognised.");
        }

        if (method is MockPaymentMethods.TimeoutNotAuthorized)
        {
            return Failure(ProviderErrorKind.Unknown, "Mock authorization timed out; nothing was authorized (scenario).");
        }

        var payment = new MockPayment(details.Reference, new ProviderPaymentRef(ProviderId, $"mockpay_{Hash(details.Reference.Value)}"), details.Amount, method, fingerprint);
        _payments.Add(details.Reference.Value, payment);
        switch (method)
        {
            case MockPaymentMethods.Declined or MockPaymentMethods.InsufficientFunds:
                payment.State = PaymentState.Declined;
                payment.DeclineReason = method is MockPaymentMethods.InsufficientFunds ? PaymentDeclineReason.InsufficientFunds : PaymentDeclineReason.Generic;
                return Success(payment);
            case MockPaymentMethods.RequiresAction:
                payment.State = PaymentState.RequiresAction;
                payment.ActionToken = new CustomerActionToken($"mock_action_{Hash(details.Reference.Value)}");
                return Success(payment);
            case MockPaymentMethods.TimeoutAuthorized:
                payment.State = PaymentState.Authorized;
                return Failure(ProviderErrorKind.Unknown, "Mock authorization timed out after authorizing (scenario).");
            default:
                payment.State = PaymentState.Authorized;
                return Success(payment);
        }
    }

    private Result<PaymentSnapshot, ProviderError> Capture(CaptureDetails details)
    {
        var operation = $"capture|{details.Reference}|{details.Amount}";
        if (Replay(details.Key, operation) is { } replayed)
        {
            return replayed ? Success(_payments[details.Reference.Value]) : Conflict();
        }

        if (Find(details.Reference, details.Payment) is not { } payment)
        {
            return Failure(ProviderErrorKind.InvalidRequest, "Unknown payment.");
        }

        // One capture per payment: a second capture, under any other key, is refused.
        if (payment.State is not PaymentState.Authorized)
        {
            return Failure(ProviderErrorKind.InvalidRequest, $"A {payment.State} payment cannot be captured.");
        }

        if (details.Amount.Currency != payment.Amount.Currency || details.Amount.Amount <= 0 || details.Amount.Amount > payment.Amount.Amount)
        {
            return Failure(ProviderErrorKind.InvalidRequest, "The capture amount must be positive, in the payment's currency, and at most the authorized amount.");
        }

        payment.State = PaymentState.Captured;
        payment.Captured = details.Amount;
        _operations[details.Key.Value] = operation;

        // The capture happened, but the answer was lost: a repeat with the same key returns it (F-23).
        return payment.Method is MockPaymentMethods.CaptureTimeout && _failedOnce.Add(details.Key.Value)
            ? Failure(ProviderErrorKind.Unknown, "Mock capture timed out after capturing (scenario).")
            : Success(payment);
    }

    private Result<PaymentSnapshot, ProviderError> Void(VoidDetails details)
    {
        var operation = $"void|{details.Reference}";
        if (Replay(details.Key, operation) is { } replayed)
        {
            return replayed ? Success(_payments[details.Reference.Value]) : Conflict();
        }

        if (Find(details.Reference, details.Payment) is not { } payment)
        {
            return Failure(ProviderErrorKind.InvalidRequest, "Unknown payment.");
        }

        switch (payment.State)
        {
            case PaymentState.Authorized or PaymentState.Voided:
                payment.State = PaymentState.Voided;
                break;
            case PaymentState.RequiresAction or PaymentState.Canceled:
                payment.State = PaymentState.Canceled; // nothing was ever held
                break;
            default:
                return Failure(ProviderErrorKind.InvalidRequest, $"A {payment.State} payment cannot be voided.");
        }

        _operations[details.Key.Value] = operation;
        return Success(payment);
    }

    private Result<PaymentRefund, ProviderError> Refund(RefundDetails details)
    {
        var operation = $"refund|{details.Reference}|{details.Amount}";
        if (Replay(details.Key, operation) is { } replayed)
        {
            return replayed
                ? Result<PaymentRefund, ProviderError>.Success(_refunds[details.Key.Value])
                : Result<PaymentRefund, ProviderError>.Failure(new ProviderError(ProviderErrorKind.IdempotencyConflict, "The operation key was already used for another operation."));
        }

        if (Find(details.Reference, details.Payment) is not { } payment)
        {
            return RefundFailure(ProviderErrorKind.InvalidRequest, "Unknown payment.");
        }

        // Refused before processing, once: the same key succeeds when retried (F-43).
        if (payment.Method is MockPaymentMethods.RefundUnavailableOnce && _failedOnce.Add(details.Key.Value))
        {
            return RefundFailure(ProviderErrorKind.Unavailable, "Mock refund unavailable; nothing was refunded (scenario).");
        }

        if (payment.State is not PaymentState.Captured
            || details.Amount.Currency != payment.Amount.Currency
            || details.Amount.Amount <= 0
            || payment.Refunded.Amount + details.Amount.Amount > payment.Captured.Amount)
        {
            return RefundFailure(ProviderErrorKind.InvalidRequest, "A refund must be positive, in the payment's currency, and the total refunded at most the captured amount.");
        }

        var status = payment.Method is MockPaymentMethods.RefundPending ? RefundStatus.Pending : RefundStatus.Succeeded;
        var refund = new PaymentRefund(details.Reference, details.Key, new ProviderRefundRef(ProviderId, $"mockre_{Hash(details.Key.Value)}"), details.Amount, status);
        payment.Refunded += details.Amount;
        _operations[details.Key.Value] = operation;
        _refunds[details.Key.Value] = refund;
        return Result<PaymentRefund, ProviderError>.Success(refund);
    }

    // true: the key already did this operation (return its result); false: it did another one (a conflict).
    private bool? Replay(OperationKey key, string operation) =>
        _operations.TryGetValue(key.Value, out var previous) ? previous == operation : null;

    private MockPayment? Find(PaymentReference reference, ProviderPaymentRef providerRef) =>
        _payments.TryGetValue(reference.Value, out var payment) && payment.ProviderRef == providerRef ? payment : null;

    // Every call runs under one lock: a real provider serialises requests with the same key the same way.
    private Task<Result<T, ProviderError>> Run<T>(CancellationToken cancellationToken, Func<Result<T, ProviderError>> operation)
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.Scenario is MockPaymentScenario.Unavailable)
        {
            return Task.FromResult(Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Mock payment provider unavailable (scenario).")));
        }

        lock (_gate)
        {
            return Task.FromResult(operation());
        }
    }

    // string.GetHashCode is randomised per process; this is stable across runs and machines.
    private static string Hash(string value) =>
        (value.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)) & int.MaxValue).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private static Result<PaymentSnapshot, ProviderError> Success(MockPayment payment) => Result<PaymentSnapshot, ProviderError>.Success(payment.Snapshot());

    private static Result<PaymentSnapshot, ProviderError> Failure(ProviderErrorKind kind, string message) =>
        Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(kind, message));

    private static Result<PaymentSnapshot, ProviderError> Conflict() =>
        Failure(ProviderErrorKind.IdempotencyConflict, "The operation key was already used for another operation.");

    private static Result<PaymentRefund, ProviderError> RefundFailure(ProviderErrorKind kind, string message) =>
        Result<PaymentRefund, ProviderError>.Failure(new ProviderError(kind, message));

    private sealed class MockPayment(PaymentReference reference, ProviderPaymentRef providerRef, Money amount, string method, string fingerprint)
    {
        public PaymentReference Reference { get; } = reference;

        public ProviderPaymentRef ProviderRef { get; } = providerRef;

        public Money Amount { get; } = amount;

        public string Method { get; } = method;

        public string Fingerprint { get; } = fingerprint;

        public PaymentState State { get; set; }

        public Money Captured { get; set; } = amount with { Amount = 0 };

        public Money Refunded { get; set; } = amount with { Amount = 0 };

        public PaymentDeclineReason? DeclineReason { get; set; }

        public CustomerActionToken? ActionToken { get; set; }

        public PaymentSnapshot Snapshot() => new(Reference, ProviderRef, State, Amount, Captured, Refunded, DeclineReason, ActionToken);
    }
}
