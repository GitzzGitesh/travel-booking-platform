using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>What a payment call established, in the core's terms (payment-lifecycle.md, booking rules).</summary>
internal abstract record PaymentOutcome
{
    private PaymentOutcome()
    {
    }

    /// <summary>The amount is held: the supplier booking may start (authorize → book → capture).</summary>
    internal sealed record Authorized(PaymentSnapshot Payment) : PaymentOutcome;

    /// <summary>The customer must complete a challenge (SCA); nothing is held yet.</summary>
    internal sealed record ActionRequired(PaymentSnapshot Payment) : PaymentOutcome;

    /// <summary>Definitive: the payment method was refused and nothing is held. Another attempt uses a new reference (F-20).</summary>
    internal sealed record Declined(PaymentDeclineReason Reason) : PaymentOutcome;

    /// <summary>The challenge was abandoned or failed, or the provider cancelled the payment; nothing is held (F-21).</summary>
    internal sealed record Canceled(PaymentSnapshot Payment) : PaymentOutcome;

    /// <summary>The hold lapsed before capture (F-24): alert; re-authorizing or cancelling is a policy decision.</summary>
    internal sealed record AuthorizationExpired(PaymentSnapshot Payment) : PaymentOutcome;

    internal sealed record Captured(PaymentSnapshot Payment) : PaymentOutcome;

    internal sealed record Voided(PaymentSnapshot Payment) : PaymentOutcome;

    internal sealed record RefundSucceeded(PaymentRefund Refund) : PaymentOutcome;

    /// <summary>Accepted but not final: it can still fail. Follow it up by our key.</summary>
    internal sealed record RefundPending(PaymentRefund Refund) : PaymentOutcome;

    /// <summary>Nothing was refunded (F-43): alert; retry with the same key after checking the refund by our key.</summary>
    internal sealed record RefundFailed(PaymentRefund Refund) : PaymentOutcome;

    /// <summary>
    /// The provider refused the request itself (invalid, or our key reused with other details). Definitive for a first
    /// attempt; after an earlier <see cref="Unknown"/> for the same operation it may mean "already done", so the caller
    /// looks the payment or refund up before acting.
    /// </summary>
    internal sealed record Rejected(ProviderErrorKind Reason) : PaymentOutcome;

    /// <summary>
    /// The write MAY have happened (timeout, same key still in progress, ambiguous or possibly-processed refusal,
    /// exception, failed lookup). Look it up by our reference or key; never assume, never re-authorize or void because of it.
    /// </summary>
    internal sealed record Unknown(ProviderErrorKind Cause) : PaymentOutcome;

    /// <summary>A lookup found nothing AS OF <paramref name="At"/>; conclusive only after the provider's consistency window.</summary>
    internal sealed record NotFound(DateTimeOffset At) : PaymentOutcome;

    /// <summary>The provider reports something other than what was asked (amount, currency, reference, provider): manual review.</summary>
    internal sealed record Mismatch(PaymentSnapshot? Payment, PaymentRefund? Refund = null) : PaymentOutcome;
}

/// <summary>
/// Makes payment provider calls for the checkout orchestration (a later chunk persists the Payment and drives it).
/// Exactly one provider write per call. The caller may repeat a write with the SAME key within the provider's
/// idempotency window (booking rules: retry only where the provider guarantees idempotency); outside it, look up first.
/// It never re-authorizes under a new reference after an unknown outcome.
/// </summary>
internal sealed partial class PaymentOperations(IPaymentProvider provider, TimeProvider timeProvider, ILogger<PaymentOperations> logger)
{
    public Task<PaymentOutcome> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken) =>
        Write(details.Reference, "authorize", () => provider.AuthorizeAsync(details, cancellationToken), payment =>
            Matches(payment, details.Reference) && payment.Amount == details.Amount ? FromState(payment) : null,
            cancellationToken);

    public Task<PaymentOutcome> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) =>
        Write(details.Reference, "capture", () => provider.CaptureAsync(details, cancellationToken), payment =>
            Matches(payment, details.Reference) && payment.State is PaymentState.Captured && payment.Captured == details.Amount
                ? new PaymentOutcome.Captured(payment)
                : null,
            cancellationToken);

    public Task<PaymentOutcome> VoidAsync(VoidDetails details, CancellationToken cancellationToken) =>
        Write(details.Reference, "void", () => provider.VoidAsync(details, cancellationToken), payment =>
            !Matches(payment, details.Reference) || payment.Captured.Amount != 0 ? null : payment.State switch
            {
                PaymentState.Voided => new PaymentOutcome.Voided(payment),

                // Voiding a payment still waiting for the customer's challenge cancels it: nothing was ever held.
                PaymentState.Canceled => new PaymentOutcome.Canceled(payment),
                _ => null,
            },
            cancellationToken);

    public Task<PaymentOutcome> RefundAsync(RefundDetails details, CancellationToken cancellationToken) =>
        Write(details.Reference, "refund", () => provider.RefundAsync(details, cancellationToken), refund =>
            refund.Payment == details.Reference && refund.Key == details.Key && refund.Amount == details.Amount && refund.Refund.ProviderId == provider.Id
                ? FromRefund(refund)
                : null,
            cancellationToken);

    /// <summary>Resolves an unknown payment outcome by our reference. A read: safe to repeat.</summary>
    public Task<PaymentOutcome> ReconcileAsync(PaymentReference reference, CancellationToken cancellationToken) =>
        Read(() => provider.RetrieveAsync(reference, cancellationToken), lookup => lookup.Payment switch
        {
            null => new PaymentOutcome.NotFound(timeProvider.GetUtcNow()),
            { } payment when Matches(payment, reference) => FromState(payment),
            { } payment => MismatchFound(new PaymentOutcome.Mismatch(payment), reference),
        });

    /// <summary>Resolves an unknown or pending refund by our key. A read: safe to repeat.</summary>
    public Task<PaymentOutcome> ReconcileRefundAsync(PaymentReference reference, OperationKey key, Money expectedAmount, CancellationToken cancellationToken) =>
        Read(() => provider.RetrieveRefundAsync(reference, key, cancellationToken), lookup => lookup.Refund switch
        {
            null => new PaymentOutcome.NotFound(timeProvider.GetUtcNow()),
            { } refund when refund.Payment == reference && refund.Key == key && refund.Amount == expectedAmount => FromRefund(refund),
            { } refund => MismatchFound(new PaymentOutcome.Mismatch(null, refund), reference),
        });

    /// <summary>
    /// Only refusals that prove nothing happened are definitive. Unavailable, RateLimited, AuthFailure and
    /// OperationInProgress on a WRITE stay unknown: they cannot prove the request was not (or will not be) processed.
    /// </summary>
    internal static PaymentOutcome Classify(ProviderError error) => error.Kind switch
    {
        ProviderErrorKind.InvalidRequest or ProviderErrorKind.IdempotencyConflict => new PaymentOutcome.Rejected(error.Kind),
        _ => new PaymentOutcome.Unknown(error.Kind),
    };

    /// <summary>What the payment's state means, whichever call reported it (a replayed authorization may be long captured).</summary>
    private static PaymentOutcome FromState(PaymentSnapshot payment) => payment.State switch
    {
        PaymentState.Authorized => new PaymentOutcome.Authorized(payment),
        PaymentState.RequiresAction => new PaymentOutcome.ActionRequired(payment),
        PaymentState.Declined => new PaymentOutcome.Declined(payment.DeclineReason ?? PaymentDeclineReason.Generic),
        PaymentState.Canceled => new PaymentOutcome.Canceled(payment),
        PaymentState.Expired => new PaymentOutcome.AuthorizationExpired(payment),
        PaymentState.Captured => new PaymentOutcome.Captured(payment),
        PaymentState.Voided => new PaymentOutcome.Voided(payment),
        _ => new PaymentOutcome.Mismatch(payment),
    };

    private static PaymentOutcome FromRefund(PaymentRefund refund) => refund.Status switch
    {
        RefundStatus.Succeeded => new PaymentOutcome.RefundSucceeded(refund),
        RefundStatus.Pending => new PaymentOutcome.RefundPending(refund),
        _ => new PaymentOutcome.RefundFailed(refund),
    };

    private async Task<PaymentOutcome> Write<T>(
        PaymentReference reference,
        string operation,
        Func<Task<Result<T, ProviderError>>> call,
        Func<T, PaymentOutcome?> expected,
        CancellationToken cancellationToken)
        where T : notnull
    {
        // Cancelled before anything is sent: nothing was attempted, so the caller's cancellation simply propagates.
        cancellationToken.ThrowIfCancellationRequested();

        Result<T, ProviderError> result;
        try
        {
            result = await call();
        }
        catch (Exception exception)
        {
            // Once the write has started, any exception may hide an operation that happened: unknown, never a failure.
            LogUnknown(logger, provider.Id, operation, reference.Value, exception.GetType().Name);
            return new PaymentOutcome.Unknown(ProviderErrorKind.Unknown);
        }

        if (!result.IsSuccess)
        {
            var classified = Classify(result.Error);
            if (classified is PaymentOutcome.Unknown)
            {
                LogUnknown(logger, provider.Id, operation, reference.Value, result.Error.Kind.ToString());
            }

            return classified;
        }

        return expected(result.Value) ?? MismatchFound(result.Value switch
        {
            PaymentSnapshot payment => new PaymentOutcome.Mismatch(payment),
            PaymentRefund refund => new PaymentOutcome.Mismatch(null, refund),
            _ => new PaymentOutcome.Mismatch(null),
        }, reference);
    }

    private async Task<PaymentOutcome> Read<T>(Func<Task<Result<T, ProviderError>>> call, Func<T, PaymentOutcome> interpret)
        where T : notnull
    {
        try
        {
            var result = await call();
            return result.IsSuccess ? interpret(result.Value) : new PaymentOutcome.Unknown(result.Error.Kind);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lookup that fails leaves the answer unknown; the reconciliation loop tries again later.
            return new PaymentOutcome.Unknown(ProviderErrorKind.Unknown);
        }
    }

    private bool Matches(PaymentSnapshot payment, PaymentReference reference) =>
        payment.Reference == reference && payment.Payment.ProviderId == provider.Id;

    private PaymentOutcome MismatchFound(PaymentOutcome.Mismatch mismatch, PaymentReference reference)
    {
        LogMismatch(logger, provider.Id, reference.Value);
        return mismatch;
    }

    // Our reference and the provider only: never tokens, card data or provider payloads (security rules).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment {Operation} at provider {ProviderId} for reference {PaymentReference} has an unknown outcome ({Cause}); it will be looked up, never re-authorized under a new reference.")]
    private static partial void LogUnknown(ILogger logger, string providerId, string operation, string paymentReference, string cause);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment at provider {ProviderId} for reference {PaymentReference} does not match what was requested; manual review required.")]
    private static partial void LogMismatch(ILogger logger, string providerId, string paymentReference);
}
