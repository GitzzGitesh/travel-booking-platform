using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Payments.Domain;

/// <summary>
/// One payment attempt (payment-lifecycle.md). Capture and refunds come with the orchestration chunk that needs them.
/// Authorization: Authorizing → any authorization outcome; ActionRequired and AuthorizationUnknown → any outcome but
/// Authorizing (a completed challenge, a lookup).
/// Release: Authorized or ActionRequired → Voiding (saved before the provider call) → Voided, Canceled (an unfinished
/// challenge), Expired (the hold had already lapsed), VoidUnknown (looked up, then voided again with the same key) or
/// ManualReview.
/// Review: ManualReview → Authorized or a status that holds nothing, as a provider lookup establishes (operations).
/// Declined, Canceled, Expired, Failed and Voided are final.
/// </summary>
internal enum PaymentAttemptStatus
{
    Authorizing,
    ActionRequired,
    AuthorizationUnknown,
    Authorized,
    Declined,
    Canceled,
    Expired,
    Failed,
    ManualReview,
    Voiding,
    VoidUnknown,
    Voided,

    /// <summary>Orders' confirmed booking is being charged: saved before the provider is asked (ADR 0005: capture after booking).</summary>
    Capturing,

    /// <summary>The capture's outcome is unknown: looked up, and repeated with the same key only while the hold is still there.</summary>
    CaptureUnknown,

    /// <summary>Charged: the money is taken (for the confirmed bookings only).</summary>
    Captured,
}

internal enum PaymentAttemptTransitionError
{
    /// <summary>The attempt's outcome is settled: a late or duplicate outcome changes nothing.</summary>
    AlreadyFinal,

    /// <summary>Not a transition this attempt can make (e.g. back to Authorizing, or voiding a declined payment).</summary>
    Illegal,
}

/// <summary>
/// One attempt to authorize an order's total. It is saved as Authorizing BEFORE the provider is called, so a crash
/// never loses a possible hold: the attempt can always be looked up by its reference. Likewise a void is saved as
/// Voiding before the provider is asked. Status changes only through the methods here, each appended to the attempt's
/// history with its actor and correlation id.
/// </summary>
internal sealed class PaymentAttempt
{
    public const int MaxCustomerIdLength = 128;
    public const int MaxReleaseReasonLength = 200;

    private readonly List<PaymentAttemptEvent> _events = [];

    private PaymentAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Unique per order: the same request returns this attempt.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    public Money Amount { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    public string? ProviderId { get; private set; }

    /// <summary>The provider's payment id, once known.</summary>
    public string? ProviderPaymentId { get; private set; }

    public string? DeclineReason { get; private set; }

    /// <summary>When Orders said it will not use this payment: the hold is to be released once its outcome is known.</summary>
    public DateTimeOffset? ReleaseRequestedAt { get; private set; }

    public string? ReleaseReason { get; private set; }

    /// <summary>When Orders asked for the charge, after its bookings were confirmed; the amount is what they cost (never more than held).</summary>
    public DateTimeOffset? CaptureRequestedAt { get; private set; }

    /// <summary>The amount to charge, in the held amount's currency (a capture never changes currency).</summary>
    public decimal? CaptureAmountValue { get; private set; }

    public Money? CaptureAmount => CaptureAmountValue is { } value ? new Money(value, Amount.Currency) : null;

    /// <summary>
    /// Our idempotency key for the one capture of this attempt: repeating it never charges twice. Like the void key, a new
    /// generation starts after each manual review, so a capture refused before the review is not replayed from the
    /// provider's idempotency cache (a capture is still at most once: the provider captures a payment only once).
    /// </summary>
    public string CaptureKey => VoidGeneration == 0 ? $"{Reference}:capture" : $"{Reference}:capture-{VoidGeneration}";

    public bool IsCaptureInProgress => Status is PaymentAttemptStatus.Capturing or PaymentAttemptStatus.CaptureUnknown;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the funds were first held (the attempt first became Authorized): the hold lapses a provider-set time later (ADR 0025).</summary>
    public DateTimeOffset? AuthorizedAt { get; private set; }

    /// <summary>When operations were warned that this hold is about to lapse (once per attempt).</summary>
    public DateTimeOffset? HoldWarningRaisedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Incremented by every change, so the rowversion check always runs.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<PaymentAttemptEvent> Events => _events;

    /// <summary>Our reference at the provider: this attempt's id, without dashes.</summary>
    public string Reference => Id.ToString("N");

    /// <summary>Our idempotency key for the one void of this attempt: repeating it never releases twice.</summary>
    public string VoidKey => VoidGeneration == 0 ? $"{Reference}:void" : $"{Reference}:void-{VoidGeneration}";

    /// <summary>
    /// How many manual reviews this attempt came out of: each starts a new void key, so a void refused before a review is
    /// not replayed from the provider's idempotency cache. A void is safe to repeat (it only releases a hold).
    /// </summary>
    public int VoidGeneration { get; private set; }

    /// <summary>
    /// Statuses in which the attempt holds, or may hold, funds: at most one such attempt per order. Each has a way out: a
    /// lookup (the open ones and the void in progress), a void (Authorized), or a person (ManualReview).
    /// </summary>
    public static IReadOnlyList<PaymentAttemptStatus> LiveStatuses { get; } =
    [
        PaymentAttemptStatus.Authorizing, PaymentAttemptStatus.ActionRequired, PaymentAttemptStatus.AuthorizationUnknown,
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.ManualReview, PaymentAttemptStatus.Voiding, PaymentAttemptStatus.VoidUnknown,
    ];

    /// <summary>The authorization's outcome is known (whatever happens to the hold afterwards).</summary>
    public bool IsAuthorizationSettled => Status is not (PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.AuthorizationUnknown);

    public bool IsVoidInProgress => Status is PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown;

    public static PaymentAttempt Start(Guid orderId, string customerId, string idempotencyKey, Money amount, PaymentChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment attempt authorizes a positive amount.");
        }

        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            IdempotencyKey = idempotencyKey,
            Amount = amount,
            Status = PaymentAttemptStatus.Authorizing,
            CreatedAt = change.At,
            UpdatedAt = change.At,
        };
        attempt.Record(null, "Authorization requested", change, null);
        return attempt;
    }

    /// <summary>
    /// Records what the provider established about the authorization. The same status again only fills in provider
    /// details, without a history entry. A settled attempt never changes this way, and nothing goes back to Authorizing.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> Resolve(
        PaymentAttemptStatus to, string reason, PaymentChange change, string? providerId = null, string? providerPaymentId = null, string? declineReason = null)
    {
        if (IsAuthorizationSettled)
        {
            return Failure(PaymentAttemptTransitionError.AlreadyFinal);
        }

        if (to is PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown or PaymentAttemptStatus.Voided)
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        if (providerId is not null && providerPaymentId is not null)
        {
            ProviderId = providerId;
            ProviderPaymentId = providerPaymentId;
        }

        DeclineReason = declineReason ?? DeclineReason;
        return MoveTo(to, reason, change, providerPaymentId);
    }

    /// <summary>
    /// Orders will not use this payment. The request is recorded (no status change) and acted on once the outcome is
    /// known. Idempotent. False when the attempt holds nothing any more, so there is nothing to release.
    /// </summary>
    public bool RequestRelease(string reason, PaymentChange change)
    {
        if (ReleaseRequestedAt is not null)
        {
            return true;
        }

        if (CaptureRequestedAt is not null)
        {
            return false; // a confirmed booking is charged, never released
        }

        if (!LiveStatuses.Contains(Status))
        {
            return false;
        }

        ReleaseRequestedAt = change.At;
        ReleaseReason = reason.Length <= MaxReleaseReasonLength ? reason : reason[..MaxReleaseReasonLength];
        Record(Status, $"Release requested: {ReleaseReason}", change, null);
        return true;
    }

    /// <summary>
    /// Orders confirmed its bookings and asks for the charge (ADR 0005: capture only after a confirmed booking). Recorded
    /// (no status change) and acted on by reconciliation. Only for an Authorized attempt with no release requested, for
    /// at most the amount held, in its currency. Idempotent for the same amount; another amount is refused.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> RequestCapture(Money amount, PaymentChange change)
    {
        if (CaptureRequestedAt is not null)
        {
            return CaptureAmount == amount ? Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status) : Failure(PaymentAttemptTransitionError.Illegal);
        }

        if (Status is not PaymentAttemptStatus.Authorized || ReleaseRequestedAt is not null
            || amount.Currency != Amount.Currency || amount.Amount <= 0 || amount.Amount > Amount.Amount || ProviderPaymentId is null)
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        CaptureRequestedAt = change.At;
        CaptureAmountValue = amount.Amount;
        Record(Status, $"Capture requested: {amount.Amount} {amount.Currency.Value} for the confirmed bookings", change, null);
        return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
    }

    /// <summary>Starts the requested capture: saved as Capturing before the provider is asked. A capture already in progress is a no-op.</summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> BeginCapture(PaymentChange change)
    {
        if (Status is PaymentAttemptStatus.Capturing)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
        }

        if (Status is not (PaymentAttemptStatus.Authorized or PaymentAttemptStatus.CaptureUnknown) || CaptureRequestedAt is null || ProviderPaymentId is null)
        {
            return Failure(Status is PaymentAttemptStatus.Captured ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(PaymentAttemptStatus.Capturing, "Charging the confirmed bookings (capture)", change, ProviderPaymentId);
    }

    /// <summary>Records the capture's outcome: Captured, CaptureUnknown (to look up), or ManualReview (refused, or not as expected).</summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> ResolveCapture(PaymentAttemptStatus to, string reason, PaymentChange change)
    {
        if (!IsCaptureInProgress)
        {
            return Failure(Status is PaymentAttemptStatus.Captured ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        if (to is not (PaymentAttemptStatus.Captured or PaymentAttemptStatus.CaptureUnknown or PaymentAttemptStatus.ManualReview))
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(to, reason, change, ProviderPaymentId);
    }

    /// <summary>
    /// Starts releasing the hold of an Authorized payment or an unfinished challenge (a void, which cancels the
    /// latter). Saved before the provider is asked. Needs the provider's payment id; a void already in progress is a no-op.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> BeginVoid(PaymentChange change)
    {
        if (Status is PaymentAttemptStatus.Voiding)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
        }

        if (Status is not (PaymentAttemptStatus.Authorized or PaymentAttemptStatus.ActionRequired or PaymentAttemptStatus.VoidUnknown)
            || ProviderId is null || ProviderPaymentId is null)
        {
            return Failure(Status is PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(PaymentAttemptStatus.Voiding, "Releasing the hold (void)", change, ProviderPaymentId);
    }

    /// <summary>The statuses a manual review may resolve to: what a lookup at the provider established.</summary>
    public static IReadOnlyList<PaymentAttemptStatus> ReviewOutcomes { get; } =
    [
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Declined, PaymentAttemptStatus.Canceled,
        PaymentAttemptStatus.Expired, PaymentAttemptStatus.Failed, PaymentAttemptStatus.Voided,
    ];

    /// <summary>
    /// Takes the attempt out of ManualReview, to what the provider was found to hold (never what someone says it holds):
    /// Authorized (a hold of this attempt's amount, then released or used as usual) or a status that holds nothing.
    /// The provider's payment found by the lookup is recorded when the attempt had none, and a different one than stored
    /// is refused; Authorized needs it, since without it the hold could never be released. A new void generation
    /// starts, so a void refused before the review is sent afresh, not replayed from the provider's idempotency cache.
    /// Only from ManualReview; the same resolution again is a no-op without a history entry.
    /// </summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> ResolveReview(
        PaymentAttemptStatus to, string reason, PaymentChange change, string? providerId = null, string? providerPaymentId = null)
    {
        if (Status == to && to != PaymentAttemptStatus.ManualReview)
        {
            return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
        }

        if (Status != PaymentAttemptStatus.ManualReview)
        {
            return Failure(LiveStatuses.Contains(Status) ? PaymentAttemptTransitionError.Illegal : PaymentAttemptTransitionError.AlreadyFinal);
        }

        if (!ReviewOutcomes.Contains(to))
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        if (providerPaymentId is not null
            && ((ProviderPaymentId is not null && ProviderPaymentId != providerPaymentId) || (ProviderId is not null && ProviderId != providerId)))
        {
            return Failure(PaymentAttemptTransitionError.Illegal); // another payment than ours: stays in review
        }

        if (to == PaymentAttemptStatus.Authorized && (ProviderPaymentId ?? providerPaymentId) is null)
        {
            return Failure(PaymentAttemptTransitionError.Illegal); // a hold we could not release
        }

        if (ProviderPaymentId is null && providerId is not null && providerPaymentId is not null)
        {
            ProviderId = providerId;
            ProviderPaymentId = providerPaymentId;
        }

        VoidGeneration++;
        return MoveTo(to, reason, change, ProviderPaymentId);
    }

    /// <summary>
    /// Records what the provider showed about this attempt without changing its status: e.g. a hold found on an attempt
    /// that is already settled, and its release. Appended to the history like any transition (never updated).
    /// </summary>
    public void RecordFinding(string reason, PaymentChange change, string? providerReference) => Record(Status, reason, change, providerReference);

    /// <summary>Records the void's outcome: Voided, Canceled (an unfinished challenge), Expired (the hold had lapsed), VoidUnknown or ManualReview.</summary>
    public Result<PaymentAttemptStatus, PaymentAttemptTransitionError> ResolveVoid(PaymentAttemptStatus to, string reason, PaymentChange change)
    {
        if (!IsVoidInProgress)
        {
            return Failure(Status is PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled ? PaymentAttemptTransitionError.AlreadyFinal : PaymentAttemptTransitionError.Illegal);
        }

        if (to is not (PaymentAttemptStatus.Voided or PaymentAttemptStatus.Canceled or PaymentAttemptStatus.Expired or PaymentAttemptStatus.VoidUnknown or PaymentAttemptStatus.ManualReview))
        {
            return Failure(PaymentAttemptTransitionError.Illegal);
        }

        return MoveTo(to, reason, change, ProviderPaymentId);
    }

    private static Result<PaymentAttemptStatus, PaymentAttemptTransitionError> Failure(PaymentAttemptTransitionError error) =>
        Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Failure(error);

    private Result<PaymentAttemptStatus, PaymentAttemptTransitionError> MoveTo(PaymentAttemptStatus to, string reason, PaymentChange change, string? providerReference)
    {
        if (to == Status)
        {
            UpdatedAt = change.At;
            Revision++;
        }
        else
        {
            var from = Status;
            Status = to;
            if (to == PaymentAttemptStatus.Authorized && AuthorizedAt is null)
            {
                // Seen authorizing now: this is when the hold began. Reached any other way (a review settled by lookup),
                // the start is unknown, so the earliest possible time is kept: the warning comes early, never late.
                AuthorizedAt = from is PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown or PaymentAttemptStatus.ActionRequired
                    ? change.At
                    : CreatedAt;
            }

            Record(from, reason, change, providerReference);
        }

        return Result<PaymentAttemptStatus, PaymentAttemptTransitionError>.Success(Status);
    }

    /// <summary>
    /// The statuses in which funds may be held: authorized, and neither captured nor released yet. A manual review may hold
    /// funds even when the attempt never showed Authorized (e.g. the provider reported a different amount).
    /// </summary>
    public static readonly PaymentAttemptStatus[] HoldingStatuses =
    [
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Capturing, PaymentAttemptStatus.CaptureUnknown,
        PaymentAttemptStatus.ManualReview, PaymentAttemptStatus.Voiding, PaymentAttemptStatus.VoidUnknown,
    ];

    public bool MayHoldFunds => HoldingStatuses.Contains(Status);

    /// <summary>When the hold began, or the earliest it can have begun (the attempt's start) when that is not known.</summary>
    public DateTimeOffset HoldStartedAt => AuthorizedAt ?? CreatedAt;

    /// <summary>Records the expiry warning once; false if it was already raised or no funds are held.</summary>
    public bool NoteHoldExpiring(DateTimeOffset at)
    {
        if (HoldWarningRaisedAt is not null || !MayHoldFunds)
        {
            return false;
        }

        HoldWarningRaisedAt = at;
        UpdatedAt = at;
        Revision++;
        return true;
    }

    private void Record(PaymentAttemptStatus? from, string reason, PaymentChange change, string? providerReference)
    {
        _events.Add(new PaymentAttemptEvent(Id, change.At, change.Actor, from?.ToString(), Status.ToString(), reason, change.CorrelationId, providerReference));
        UpdatedAt = change.At;
        Revision++;
    }
}

/// <summary>Who changed an attempt, and when, with the request's correlation id (security rules: payments are audited).</summary>
internal sealed record PaymentChange(DateTimeOffset At, string Actor, string? CorrelationId);

/// <summary>An append-only record of one attempt transition. Never updated or deleted.</summary>
internal sealed record PaymentAttemptEvent(
    Guid PaymentAttemptId, DateTimeOffset At, string Actor, string? FromStatus, string ToStatus, string Reason, string? CorrelationId, string? ProviderReference)
{
    public long Id { get; private set; }
}
