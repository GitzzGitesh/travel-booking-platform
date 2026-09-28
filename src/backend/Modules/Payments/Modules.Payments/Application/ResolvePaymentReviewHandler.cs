using Microsoft.Extensions.Options;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <param name="OperatorId">
/// The staff member resolving the review: an opaque staff id (never a name or email). Until staff identity exists it
/// is given by the caller; the admin endpoint must take it from the staff principal (docs/progress.md, row 4d2).
/// </param>
/// <param name="Reason">
/// Why they resolve it now: a ticket reference and a short note, kept on the append-only history, so it must hold no
/// personal or card data (Q9). Printable text, 1 to <see cref="ResolvePaymentReviewHandler.MaxReasonLength"/> characters.
/// </param>
internal sealed record ResolvePaymentReview(Guid AttemptId, string OperatorId, string Reason, string? CorrelationId = null);

internal enum PaymentReviewOutcome
{
    /// <summary>The attempt left ManualReview, to what the provider holds.</summary>
    Resolved,

    /// <summary>It was not (or no longer) in ManualReview: nothing changed (a repeated or late request).</summary>
    AlreadyResolved,

    /// <summary>The provider's answer does not settle it (captured, another amount, not found yet, lookup failed): still in review.</summary>
    StillNeedsReview,

    NotFound,

    /// <summary>No operator or reason given.</summary>
    Invalid,
}

internal sealed record PaymentReviewResult(PaymentReviewOutcome Outcome, PaymentAttemptStatus? Status);

/// <summary>
/// The way out of ManualReview for a payment attempt (the minimum for checkout to become customer-facing; the admin UI
/// comes with the operations batch). The operator never states the outcome: the attempt is looked up with the provider
/// by our reference and moves only to what the provider holds. Idempotent, and recorded on the attempt's history with
/// the operator as actor and their reason (audit). A hold found this way is then released or used as usual (a release
/// request makes reconciliation void it). Anything the lookup cannot settle stays in review, and the check is recorded.
/// </summary>
internal sealed class ResolvePaymentReviewHandler(
    IPaymentAttemptStore store,
    PaymentOperations operations,
    TimeProvider timeProvider,
    IOptions<PaymentReconciliationOptions> reconciliation)
{
    public const int MaxOperatorIdLength = 64;
    public const int MaxReasonLength = 200;

    public async Task<PaymentReviewResult> HandleAsync(ResolvePaymentReview command, CancellationToken cancellationToken)
    {
        if (command.OperatorId is not { Length: > 0 and <= MaxOperatorIdLength } || !command.OperatorId.All(c => c is > ' ' and <= '~')
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > MaxReasonLength || command.Reason.Any(char.IsControl))
        {
            return new PaymentReviewResult(PaymentReviewOutcome.Invalid, null);
        }

        if (await store.FindAsync(command.AttemptId, cancellationToken) is not { } attempt)
        {
            return new PaymentReviewResult(PaymentReviewOutcome.NotFound, null);
        }

        if (attempt.Status != PaymentAttemptStatus.ManualReview)
        {
            return new PaymentReviewResult(PaymentReviewOutcome.AlreadyResolved, attempt.Status);
        }

        var outcome = await operations.ReconcileAsync(new PaymentReference(attempt.Reference), attempt.KnownProviderPayment(), cancellationToken);
        var change = new PaymentChange(timeProvider.GetUtcNow(), $"operator:{command.OperatorId}", command.CorrelationId);
        var reason = command.Reason;
        var found = SnapshotOf(outcome);
        if (Target(attempt, outcome) is not { } target
            || !attempt.ResolveReview(target, $"Review resolved by operator ({reason}); the provider reports {outcome.GetType().Name}", change, found?.Payment.ProviderId, found?.Payment.Value).IsSuccess)
        {
            // Unsettled, or not this attempt's payment, or a hold we could not release: the check is recorded.
            attempt.RecordFinding($"Review checked by operator ({reason}); the provider reports {outcome.GetType().Name}: still in review", change, found?.Payment.Value ?? attempt.ProviderPaymentId);
            await store.TrySaveAsync(cancellationToken);
            return new PaymentReviewResult(PaymentReviewOutcome.StillNeedsReview, attempt.Status);
        }

        if (!await store.TrySaveAsync(cancellationToken))
        {
            // Another request changed the attempt first: report what it is now.
            var current = await store.FindAsync(command.AttemptId, cancellationToken);
            return current is { Status: not PaymentAttemptStatus.ManualReview }
                ? new PaymentReviewResult(PaymentReviewOutcome.AlreadyResolved, current.Status)
                : new PaymentReviewResult(PaymentReviewOutcome.StillNeedsReview, current?.Status);
        }

        return new PaymentReviewResult(PaymentReviewOutcome.Resolved, attempt.Status);
    }

    // What the provider's answer settles. A hold of another amount, a capture, a challenge or a failed lookup settle
    // nothing. "Not found" settles it only when the provider never acknowledged the payment (no id of theirs is known)
    // and its consistency window has passed: a payment it once had and no longer finds (another account or key?) is
    // exactly what a person must look at, and calling it Failed would free the order for a second hold.
    private PaymentAttemptStatus? Target(PaymentAttempt attempt, PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Authorized authorized when authorized.Payment.Amount == attempt.Amount => PaymentAttemptStatus.Authorized,
        PaymentOutcome.Declined => PaymentAttemptStatus.Declined,
        PaymentOutcome.Canceled => PaymentAttemptStatus.Canceled,
        PaymentOutcome.AuthorizationExpired => PaymentAttemptStatus.Expired,
        PaymentOutcome.Voided => PaymentAttemptStatus.Voided,
        PaymentOutcome.NotFound notFound when attempt.ProviderPaymentId is null
            && notFound.At - attempt.CreatedAt >= reconciliation.Value.NotFoundConclusiveAfter => PaymentAttemptStatus.Failed,
        _ => null,
    };

    private static PaymentSnapshot? SnapshotOf(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Authorized o => o.Payment,
        PaymentOutcome.Canceled o => o.Payment,
        PaymentOutcome.AuthorizationExpired o => o.Payment,
        PaymentOutcome.Voided o => o.Payment,
        _ => null,
    };
}
