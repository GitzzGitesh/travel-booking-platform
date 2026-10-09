using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock;

/// <summary>
/// A deterministic payment provider: no network, no randomness, no card data. The outcome is chosen by the payment
/// method token (<see cref="MockPaymentMethods"/>), so one running Api can show every scenario. Payments are kept in
/// the <see cref="IMockPaymentLedger"/>: in SQL by default, so the Api and the Worker see the same payments (ADR 0032),
/// Development and Staging only. Idempotent by our keys, as a real provider is.
/// </summary>
internal sealed class MockPaymentProvider(IOptions<MockPaymentProviderOptions> options, IMockPaymentLedger ledger) : IPaymentProvider
{
    public const string ProviderId = "mockpay";

    public string Id => ProviderId;

    // The outcomes a customer can complete in the browser without a challenge (the mock has no challenge page).
    public PaymentEntry Entry { get; } = new(PaymentEntryMode.Test,
    [
        new TestPaymentMethod(MockPaymentMethods.Approved, "Test payment: approved"),
        new TestPaymentMethod(MockPaymentMethods.Declined, "Test payment: declined"),
        new TestPaymentMethod(MockPaymentMethods.TimeoutAuthorized, "Test payment: slow answer, then approved"),
    ]);

    public Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken) =>
        Run(details.Reference, null, entry => Authorize(details, entry), cancellationToken);

    public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) =>
        Run(details.Reference, details.Key, entry => Capture(details, entry), cancellationToken);

    public Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken) =>
        Run(details.Reference, details.Key, entry => Void(details, entry), cancellationToken);

    public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) =>
        Run(details.Reference, details.Key, entry => Refund(details, entry), cancellationToken);

    public Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken) =>
        Run(reference, null, entry => Result<PaymentLookup, ProviderError>.Success(
            new PaymentLookup(entry.Payment is { } payment ? AfterChallenge(payment).Snapshot() : null)), cancellationToken);

    public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) =>
        Run(reference, key, entry => Result<RefundLookup, ProviderError>.Success(
            new RefundLookup(entry.Refund is { } refund && refund.Payment == reference ? refund : null)), cancellationToken);

    private static Result<PaymentSnapshot, ProviderError> Authorize(AuthorizationDetails details, MockLedgerEntry entry)
    {
        var fingerprint = $"{Canonical(details.Amount)}|{details.PaymentMethod.Value}";
        if (entry.Payment is { } existing)
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
        entry.Payment = payment;
        switch (method)
        {
            case MockPaymentMethods.Declined or MockPaymentMethods.InsufficientFunds:
                payment.State = PaymentState.Declined;
                payment.DeclineReason = method is MockPaymentMethods.InsufficientFunds ? PaymentDeclineReason.InsufficientFunds : PaymentDeclineReason.Generic;
                return Success(payment);
            case MockPaymentMethods.RequiresAction or MockPaymentMethods.ChallengeCompleted or MockPaymentMethods.ChallengeFailed:
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

    private static Result<PaymentSnapshot, ProviderError> Capture(CaptureDetails details, MockLedgerEntry entry)
    {
        var operation = $"capture|{details.Reference}|{Canonical(details.Amount)}";
        if (Replay(entry, operation) is { } replayed)
        {
            return replayed && entry.Payment is { } done ? Success(done) : Conflict();
        }

        if (Find(entry, details.Payment) is not { } payment)
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
        entry.Operation = operation;

        // The capture happened, but the answer was lost: a repeat with the same key returns it (F-23).
        if (payment.Method is MockPaymentMethods.CaptureTimeout && !entry.FailedOnce)
        {
            entry.FailedOnce = true;
            return Failure(ProviderErrorKind.Unknown, "Mock capture timed out after capturing (scenario).");
        }

        return Success(payment);
    }

    private static Result<PaymentSnapshot, ProviderError> Void(VoidDetails details, MockLedgerEntry entry)
    {
        var operation = $"void|{details.Reference}";
        if (Replay(entry, operation) is { } replayed)
        {
            return replayed && entry.Payment is { } done ? Success(done) : Conflict();
        }

        if (Find(entry, details.Payment) is not { } payment)
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

        entry.Operation = operation;
        return Success(payment);
    }

    private static Result<PaymentRefund, ProviderError> Refund(RefundDetails details, MockLedgerEntry entry)
    {
        var operation = $"refund|{details.Reference}|{Canonical(details.Amount)}";
        if (Replay(entry, operation) is { } replayed)
        {
            return replayed && entry.Refund is { } done
                ? Result<PaymentRefund, ProviderError>.Success(done)
                : Result<PaymentRefund, ProviderError>.Failure(new ProviderError(ProviderErrorKind.IdempotencyConflict, "The operation key was already used for another operation."));
        }

        if (Find(entry, details.Payment) is not { } payment)
        {
            return RefundFailure(ProviderErrorKind.InvalidRequest, "Unknown payment.");
        }

        // Refused before processing, once: the same key succeeds when retried (F-43).
        if (payment.Method is MockPaymentMethods.RefundUnavailableOnce && !entry.FailedOnce)
        {
            entry.FailedOnce = true;
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
        entry.Operation = operation;
        entry.Refund = refund;
        return Result<PaymentRefund, ProviderError>.Success(refund);
    }

    // The customer's challenge happens in their browser, between the authorization and the next lookup.
    private static MockPayment AfterChallenge(MockPayment payment)
    {
        if (payment.State is PaymentState.RequiresAction && payment.Method is MockPaymentMethods.ChallengeCompleted or MockPaymentMethods.ChallengeFailed)
        {
            payment.State = payment.Method is MockPaymentMethods.ChallengeCompleted ? PaymentState.Authorized : PaymentState.Declined;
            payment.DeclineReason = payment.State is PaymentState.Declined ? PaymentDeclineReason.Generic : null;
            payment.ActionToken = null;
        }

        return payment;
    }

    // true: the key already did this operation (return its result); false: it did another one (a conflict).
    private static bool? Replay(MockLedgerEntry entry, string operation) =>
        entry.Operation is { } previous ? previous == operation : null;

    private static MockPayment? Find(MockLedgerEntry entry, ProviderPaymentRef providerRef) =>
        entry.Payment is { } payment && payment.ProviderRef == providerRef ? payment : null;

    // Every call runs serialised with the other calls for its payment (the ledger's guarantee), as a real provider
    // serialises requests with the same key. A ledger that cannot be read or kept is the provider being unavailable,
    // or, once its write may have happened, an unknown outcome to reconcile (booking-and-payments rules).
    private async Task<Result<T, ProviderError>> Run<T>(
        PaymentReference reference, OperationKey? key, Func<MockLedgerEntry, Result<T, ProviderError>> operation, CancellationToken cancellationToken)
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.Scenario is MockPaymentScenario.Unavailable)
        {
            return Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Mock payment provider unavailable (scenario)."));
        }

        try
        {
            return await ledger.RunAsync(reference, key, operation, cancellationToken);
        }
        catch (MockLedgerException failure)
        {
            return Result<T, ProviderError>.Failure(new ProviderError(failure.Kind, failure.Message));
        }
    }

    // An amount as it is kept and compared across processes (ADR 0032): culture-free and scale-free, so 120, 120.00 and
    // 120.0000 (as read back from a decimal(19,4) column) are the same amount in any culture.
    private static string Canonical(Money amount) =>
        $"{(amount.Amount / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture)} {amount.Currency.Value}";

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
}
