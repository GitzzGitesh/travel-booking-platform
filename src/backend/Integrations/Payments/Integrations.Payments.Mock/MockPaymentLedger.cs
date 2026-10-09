using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock;

/// <summary>
/// Where the mock keeps its payments (ADR 0032). Every provider call reads and changes the state of one payment
/// reference, and of the operation key it was given, and runs serialised with every other call for that reference, as a
/// real provider serialises requests. <see cref="Persistence.SqlMockPaymentLedger"/> shares that state between the Api and the
/// Worker; <see cref="InProcessMockPaymentLedger"/> keeps it in one process (unit and contract tests).
/// </summary>
internal interface IMockPaymentLedger
{
    /// <summary>
    /// Runs <paramref name="operation"/> on what is stored for <paramref name="reference"/> and <paramref name="key"/>,
    /// then keeps what it changed. Throws <see cref="MockLedgerException"/> when the state cannot be read or kept.
    /// </summary>
    Task<T> RunAsync<T>(PaymentReference reference, OperationKey? key, Func<MockLedgerEntry, T> operation, CancellationToken cancellationToken);
}

/// <summary>What one call can see and change: the payment, and what its operation key already did.</summary>
internal sealed class MockLedgerEntry
{
    /// <summary>The payment under the call's reference; set by an authorization that creates it.</summary>
    public MockPayment? Payment { get; set; }

    /// <summary>What the operation key did ("capture|ref|amount"), once it did something.</summary>
    public string? Operation { get; set; }

    /// <summary>The refund the operation key made.</summary>
    public PaymentRefund? Refund { get; set; }

    /// <summary>The key's "fails once" scenario already failed (the retry under the same key then succeeds).</summary>
    public bool FailedOnce { get; set; }
}

/// <summary>The state could not be read or kept. <see cref="Kind"/> says whether anything may have happened.</summary>
internal sealed class MockLedgerException(ProviderErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary><see cref="ProviderErrorKind.Unavailable"/>: nothing was kept. <see cref="ProviderErrorKind.Unknown"/>: it may have been.</summary>
    public ProviderErrorKind Kind { get; } = kind;
}

/// <summary>A mock payment as the provider holds it. Mutable: a call changes it, and the ledger keeps the change.</summary>
internal sealed class MockPayment(PaymentReference reference, ProviderPaymentRef providerRef, Money amount, string method, string fingerprint)
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

/// <summary>The state in this process only, under one lock (unit and contract tests; the mock as it always was).</summary>
internal sealed class InProcessMockPaymentLedger : IMockPaymentLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MockPayment> _payments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _operations = new(StringComparer.Ordinal); // key -> what it did
    private readonly Dictionary<string, PaymentRefund> _refunds = new(StringComparer.Ordinal); // key -> refund
    private readonly HashSet<string> _failedOnce = new(StringComparer.Ordinal);

    public Task<T> RunAsync<T>(PaymentReference reference, OperationKey? key, Func<MockLedgerEntry, T> operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = new MockLedgerEntry
            {
                Payment = _payments.GetValueOrDefault(reference.Value),
                Operation = key is { } k ? _operations.GetValueOrDefault(k.Value) : null,
                Refund = key is { } r ? _refunds.GetValueOrDefault(r.Value) : null,
                FailedOnce = key is { } f && _failedOnce.Contains(f.Value),
            };

            var result = operation(entry);

            if (entry.Payment is { } payment)
            {
                _payments[reference.Value] = payment;
            }

            if (key is { } done)
            {
                if (entry.Operation is { } what)
                {
                    _operations[done.Value] = what;
                }

                if (entry.Refund is { } refund)
                {
                    _refunds[done.Value] = refund;
                }

                if (entry.FailedOnce)
                {
                    _failedOnce.Add(done.Value);
                }
            }

            return Task.FromResult(result);
        }
    }
}
