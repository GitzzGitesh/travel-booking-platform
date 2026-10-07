using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.Application;

internal interface IRefundCaseStore
{
    void Add(RefundCase refundCase);

    /// <summary>
    /// Tracked, for a change. Load the case's order first anyway (defence in depth): a retried order load detaches only
    /// the order's own copy, so a case loaded before it stays tracked.
    /// </summary>
    Task<RefundCase?> FindAsync(Guid caseId, CancellationToken cancellationToken);

    /// <summary>Read-only: which order a case belongs to, before that order is loaded.</summary>
    Task<RefundCase?> PeekAsync(Guid caseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefundCase>> FindForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Cases waiting for a second person, oldest first (the approvers' list).</summary>
    Task<IReadOnlyList<RefundCase>> FindPendingAsync(int limit, CancellationToken cancellationToken);

    Task<RefundCase?> FindByKeyAsync(string staffId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Approved cases decided before <paramref name="decidedBefore"/> and not yet alerted on (tracked, for marking).</summary>
    Task<IReadOnlyList<RefundCase>> FindOverdueAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken);

    Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken);

    void MarkConsumed(Guid messageId, string handler, DateTimeOffset at);
}

/// <summary>
/// <c>Refunds</c> (ADR 0027). <see cref="CancellationFee"/> (per ISO currency): our fee kept on a cancellation; none by
/// default, and only once disclosed in the terms at booking (pending legal confirmation). Every refund opened by staff
/// needs a second person (ADR 0027 §5); a threshold below which none is needed is for customer self-service later, not
/// configured now. <see cref="ExecutionTargetDays"/>: an approved refund not refunded by then is alerted on.
/// </summary>
internal sealed class RefundOptions
{
    public const string SectionName = "Refunds";

    public Dictionary<string, decimal> CancellationFee { get; set; } = new(StringComparer.Ordinal);

    public int ExecutionTargetDays { get; set; } = 7;

    public bool IsValid() => CancellationFee.Values.All(v => v >= 0) && ExecutionTargetDays > 0;

    public Money FeeFor(CurrencyCode currency) => new(CancellationFee.GetValueOrDefault(currency.Value), currency);
}

/// <summary>The acting staff member: our staff id and their workforce account, from the validated staff identity only.</summary>
internal sealed record RefundActor(string StaffId, string Account);

/// <param name="ItemIds">The confirmed items cancelled at the supplier (a cancellation).</param>
/// <param name="SupplierRefund">What the supplier refunds us for them, as its desk stated (a cancellation).</param>
/// <param name="Amount">The refund asked for (goodwill only; a cancellation's amount is computed).</param>
internal sealed record OpenRefundCase(
    Guid OrderId, RefundCaseKind Kind, IReadOnlyList<Guid> ItemIds, string? SupplierReference, decimal? SupplierRefund, decimal? Amount,
    string Reason, string IdempotencyKey, RefundActor Actor, AuditSource Source)
{
    /// <summary>What is asked for: the same idempotency key must always ask for exactly this.</summary>
    public string Fingerprint =>
        string.Join('|', OrderId.ToString("N"), Kind, string.Join(',', ItemIds.Order().Select(i => i.ToString("N"))), SupplierReference ?? string.Empty,
            SupplierRefund?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    public bool IsValid() =>
        AuditReasons.IsValid(Reason) && Actor.StaffId is { Length: > 0 and <= 64 } && Actor.Account is { Length: > 0 and <= 128 }
        && IdempotencyKey is { Length: > 0 and <= RefundCase.MaxKeyLength } && IdempotencyKey.All(c => c is > ' ' and <= '~')
        && Kind switch
        {
            RefundCaseKind.Cancellation => ItemIds.Count > 0 && ItemIds.Distinct().Count() == ItemIds.Count && AuditReasons.IsValid(SupplierReference)
                && SupplierRefund is >= 0 && Amount is null,
            RefundCaseKind.Goodwill => ItemIds.Count == 0 && Amount is > 0 && SupplierRefund is null,
            _ => false,
        };
}

internal enum RefundCaseOutcome
{
    Done,

    /// <summary>The same key and request again: the case it opened (nothing new).</summary>
    Replayed,

    /// <summary>The same key for another request: refused.</summary>
    IdempotencyConflict,
    Invalid,
    NotFound,

    /// <summary>Nothing was captured for this order, so there is nothing to refund.</summary>
    NotCaptured,

    /// <summary>An item is not a confirmed booking of this order (a cancellation).</summary>
    ItemNotCancellable,

    /// <summary>The refund exceeds what can still be refunded (other refunds or open cases hold the rest).</summary>
    ExceedsRefundable,

    SelfApproval,
    NotRequester,
    AlreadyDecided,
    Expired,
    Conflict,
}

/// <summary>
/// Cancellations and refunds (ADR 0027). A cancellation is recorded as a fact (the items become Cancelled, with the
/// supplier desk's reference) together with its refund case; the refund's amount is computed by the server: what the
/// supplier refunds for those items (never more than the customer paid for them), less our disclosed fee, never more than
/// can still be refunded. A different person approves it unless the policy lets it through; only then does Payments refund
/// it, once, under the case's id. A hotel stay's refund follows the booked rate's agreed terms instead (ADR 0030 §7).
/// Every step is audited in the same save.
/// </summary>
internal sealed partial class RefundCaseHandler(
    IOrderStore orders, IRefundCaseStore cases, ICancellationRequestStore requests, IOrderPayments payments, IOptions<RefundOptions> options,
    TimeProvider timeProvider, ILogger<RefundCaseHandler> logger)
{
    public const string OpenAction = "refunds.open";
    public const string ApproveAction = "refunds.approve";
    public const string RejectAction = "refunds.reject";
    public const int MaxPending = 100;

    public async Task<(RefundCaseOutcome Outcome, RefundCase? Case)> OpenAsync(OpenRefundCase command, CancellationToken cancellationToken)
    {
        if (!command.IsValid())
        {
            return (RefundCaseOutcome.Invalid, null);
        }

        // Idempotency (non-negotiable 4): the same key and request returns the case it opened; another request is refused.
        if (await Replay(command, cancellationToken) is { } replay)
        {
            return replay;
        }

        if (await orders.FindAsync(command.OrderId, cancellationToken) is not { } order)
        {
            return (RefundCaseOutcome.NotFound, null);
        }

        if (await payments.FindRefundableAsync(order.Id, cancellationToken) is not { } balance)
        {
            return (RefundCaseOutcome.NotCaptured, null);
        }

        // What can still be refunded: captured, less refunds made or under way, less other cases that still hold money
        // (approved, or pending within their window). Payments checks again on its own record when it refunds.
        var now = timeProvider.GetUtcNow();
        var open = (await cases.FindForOrderAsync(order.Id, cancellationToken)).Where(c => c.HoldsRefundable(now) && c.Status is not RefundCaseStatus.Refunded)
            .Sum(c => c.AmountValue);
        var available = Math.Max(0, balance.Refundable.Amount - open);
        var currency = balance.Captured.Currency;
        var staff = $"staff:{command.Actor.StaffId}";
        var context = new TransitionContext(now, staff, command.Source.CorrelationId);

        Money amount;
        Money fee = new(0, currency);
        Money? supplierRefund = null;
        var basis = string.Empty;
        if (command.Kind is RefundCaseKind.Cancellation)
        {
            var items = command.ItemIds.Select(id => order.Items.SingleOrDefault(i => i.Id == id)).ToList();
            if (items.Any(i => i is not { Status: FlightOrderItemStatus.Confirmed } || i.AgreedPrice.Currency != currency))
            {
                return (RefundCaseOutcome.ItemNotCancellable, null);
            }

            var paidForItems = items.Sum(i => i!.AgreedPrice.Amount);
            supplierRefund = new Money(command.SupplierRefund!.Value, currency);
            decimal computed;
            if (items.Any(i => i!.Product is OrderProduct.Hotel))
            {
                // A hotel stay is refunded by the booked rate's terms (ADR 0030 §7), as the customer agreed to them, at the
                // moment they asked to cancel (their open request), else now: a desk delay never costs them the deadline.
                // Never less than the supplier returns us for it (a cancellation by the hotel or the supplier, a walk), and
                // no fee: free cancellation was advertised. Items booked before terms were recorded follow the supplier's refund.
                if (items.Any(i => i!.Product is not OrderProduct.Hotel))
                {
                    return (RefundCaseOutcome.ItemNotCancellable, null);
                }

                var request = await requests.FindLatestForOrderAsync(order.Id, cancellationToken);
                var askedAt = request is { Status: CancellationRequestStatus.Open } ? request.RequestedAt : now;
                var byTerms = items.Sum(i => i!.CancellationTerms?.RefundOf(i.AgreedPrice.Amount, askedAt) ?? 0);
                var bySupplier = Math.Min(command.SupplierRefund.Value, paidForItems);
                computed = Math.Max(byTerms, bySupplier);
                basis = items.All(i => i!.CancellationTerms is not null)
                    ? $"; by the rate's terms ({string.Join("; ", items.Select(i => i!.CancellationTerms!.Describe(currency.Value)))}), asked {askedAt:O}"
                      + (bySupplier > byTerms ? $", raised to the supplier's refund {bySupplier}" : string.Empty)
                    : "; terms not recorded for this booking: by the supplier's refund";
                if (command.SupplierRefund.Value < computed)
                {
                    // We refund more than the supplier returns (a late desk cancellation): visible, never taken from the customer.
                    basis += $"; shortfall {computed - command.SupplierRefund.Value} {currency.Value} against the supplier's refund";
                    LogHotelCancellationShortfall(logger, order.Id, computed - command.SupplierRefund.Value);
                }
            }
            else
            {
                fee = options.Value.FeeFor(currency);
                computed = Math.Max(0, Math.Min(command.SupplierRefund.Value, paidForItems) - fee.Amount);
            }

            if (computed > available)
            {
                // Never shrunk silently: other refunds or open cases hold the money; settle them first.
                return (RefundCaseOutcome.ExceedsRefundable, null);
            }

            foreach (var item in items)
            {
                if (!order.CancelConfirmed(item!.Id, command.SupplierReference!, context).IsSuccess)
                {
                    return (RefundCaseOutcome.ItemNotCancellable, null);
                }
            }

            amount = new Money(computed, currency);
        }
        else
        {
            if (command.Amount!.Value > available)
            {
                return (RefundCaseOutcome.ExceedsRefundable, null);
            }

            amount = new Money(command.Amount.Value, currency);
        }

        // Every refund opened by staff waits for a second person (ADR 0027 §5).
        var refundCase = RefundCase.Open(order.Id, balance.PaymentId, command.Kind, command.ItemIds, amount, supplierRefund, fee,
            command.SupplierReference, command.Reason, command.Actor.StaffId, command.Actor.Account, command.IdempotencyKey, command.Fingerprint, now);
        cases.Add(refundCase);
        order.NoteRefund(command.ItemIds, $"Refund case {refundCase.Id:N} opened: {refundCase.Kind}, {amount.Amount} {amount.Currency.Value} ({refundCase.Status}){basis}",
            context, command.SupplierReference);
        if (command.Kind is RefundCaseKind.Cancellation)
        {
            // The customer's request, if any (ADR 0029): answered by this case once nothing confirmed is left; a request
            // withdrawn before the desk cancelled is flagged for a person to tell the customer.
            var latest = await requests.FindLatestForOrderAsync(order.Id, cancellationToken);
            if (latest is { Status: CancellationRequestStatus.Open } && !order.Items.Any(i => i.Status is FlightOrderItemStatus.Confirmed)
                && await requests.FindOpenForOrderAsync(order.Id, cancellationToken) is { } request && request.Complete(refundCase.Id, staff, now))
            {
                order.NoteRefund([], $"Cancellation request {request.Id:N} completed by refund case {refundCase.Id:N}", context, null);
            }
            else if (latest is { Status: CancellationRequestStatus.Open })
            {
                order.NoteRefund([], $"Cancellation request {latest.Id:N} stays open: confirmed items remain", context, null);
            }
            else if (latest is { Status: CancellationRequestStatus.Withdrawn })
            {
                order.NoteRefund([], $"Attention: the customer had withdrawn cancellation request {latest.Id:N}; confirm with them", context, null);
            }

            orders.Publish(new OrderCancellationRecorded(Guid.NewGuid(), now, order.Id, refundCase.Id, amount, command.Source.CorrelationId), command.Source.CorrelationId);
        }

        orders.Audit(AuditEntry.For(command.Source, now, staff, OpenAction, $"refund-case:{refundCase.Id}", null,
            $"{refundCase.Kind} {amount.Amount} {amount.Currency.Value} ({refundCase.Status}; supplier refund {supplierRefund?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, fee {fee.Amount}, items {refundCase.ItemIds}, supplier reference {command.SupplierReference ?? "-"}{basis}); {command.Reason}"));
        if (await orders.TrySaveAsync(cancellationToken))
        {
            return (RefundCaseOutcome.Done, refundCase);
        }

        // A concurrent request with the same key won (unique index): answer with its case.
        return await Replay(command, cancellationToken) ?? (RefundCaseOutcome.Conflict, null);
    }

    private async Task<(RefundCaseOutcome Outcome, RefundCase? Case)?> Replay(OpenRefundCase command, CancellationToken cancellationToken) =>
        await cases.FindByKeyAsync(command.Actor.StaffId, command.IdempotencyKey, cancellationToken) is { } existing
            ? existing.RequestFingerprint == command.Fingerprint ? (RefundCaseOutcome.Replayed, existing) : (RefundCaseOutcome.IdempotencyConflict, existing)
            : null;

    /// <summary>A checker approves or rejects; the requester may only withdraw (reject) their own case.</summary>
    public async Task<(RefundCaseOutcome Outcome, RefundCase? Case)> DecideAsync(
        Guid caseId, bool approve, string reason, RefundActor actor, bool isChecker, AuditSource source, CancellationToken cancellationToken)
    {
        if (!AuditReasons.IsValid(reason) || actor.StaffId is not { Length: > 0 and <= 64 } || actor.Account is not { Length: > 0 and <= 128 })
        {
            return (RefundCaseOutcome.Invalid, null);
        }

        // The order first, then the tracked case (defence in depth: a retried order load detaches only the order's own copy;
        // before that change it cleared everything, and a case loaded first lost its decision while the rest was saved).
        if (await cases.PeekAsync(caseId, cancellationToken) is not { } peeked)
        {
            return (RefundCaseOutcome.NotFound, null);
        }

        var order = await orders.FindAsync(peeked.OrderId, cancellationToken);
        if (await cases.FindAsync(caseId, cancellationToken) is not { } refundCase)
        {
            return (RefundCaseOutcome.NotFound, null);
        }

        if (!isChecker && !(refundCase.IsRequester(actor.StaffId, actor.Account) && !approve))
        {
            LogRefused(logger, actor.StaffId, refundCase.Id, "not-the-requester");
            return (RefundCaseOutcome.NotRequester, refundCase);
        }

        var now = timeProvider.GetUtcNow();
        switch (approve ? refundCase.Approve(actor.StaffId, actor.Account, reason, now) : refundCase.Reject(actor.StaffId, reason, now))
        {
            case RefundCaseRefusal.SelfApproval:
                LogRefused(logger, actor.StaffId, refundCase.Id, "self-approval");
                return (RefundCaseOutcome.SelfApproval, refundCase);
            case RefundCaseRefusal.AlreadyDecided:
                return (RefundCaseOutcome.AlreadyDecided, refundCase);
            case RefundCaseRefusal.Expired:
                return (RefundCaseOutcome.Expired, refundCase);
        }

        if (approve)
        {
            RequestRefund(refundCase, now, source.CorrelationId);
        }

        if (order is not null)
        {
            order.NoteRefund(refundCase.CancelledItemIds, $"Refund case {refundCase.Id:N} {refundCase.Status.ToString().ToLowerInvariant()}: {reason}",
                new TransitionContext(now, $"staff:{actor.StaffId}", source.CorrelationId), null);
        }

        orders.Audit(AuditEntry.For(source, now, $"staff:{actor.StaffId}", approve ? ApproveAction : RejectAction, $"refund-case:{refundCase.Id}",
            nameof(RefundCaseStatus.PendingApproval), $"{refundCase.Status} ({refundCase.Amount.Amount} {refundCase.Amount.Currency.Value}); {reason}"));
        return await orders.TrySaveAsync(cancellationToken) ? (RefundCaseOutcome.Done, refundCase) : (RefundCaseOutcome.Conflict, null);
    }

    private void RequestRefund(RefundCase refundCase, DateTimeOffset at, string? correlationId) =>
        orders.Publish(new OrderRefundRequested(Guid.NewGuid(), at, refundCase.OrderId, refundCase.PaymentId, refundCase.Id, refundCase.Amount, correlationId),
            correlationId);

    // Security event (ADR 0022: maker-checker refusals): ids only.
    [LoggerMessage(Level = LogLevel.Warning, EventName = "HotelCancellationShortfall",
        Message = "Alert: order {OrderId}'s hotel cancellation refunds {Shortfall} more than the supplier returns (desk delay or supplier penalty); review the desk's timing")]
    private static partial void LogHotelCancellationShortfall(ILogger logger, Guid orderId, decimal shortfall);

    [LoggerMessage(Level = LogLevel.Warning, EventName = "RefundDecisionRefused",
        Message = "Security: staff member {StaffId} was refused on refund case {CaseId} ({Why})")]
    private static partial void LogRefused(ILogger logger, string staffId, Guid caseId, string why);
}

/// <summary>Payments' final outcome for a refund (ADR 0027; inbox: once per event): the case becomes Refunded or RefundFailed.</summary>
internal sealed partial class PaymentRefundSettledHandler(IOrderStore orders, IRefundCaseStore cases, TimeProvider timeProvider, ILogger<PaymentRefundSettledHandler> logger)
    : IIntegrationEventHandler<PaymentRefundSettled>
{
    public const string Name = "orders.payment-refund-settled";
    public const string Actor = "system:payments";

    public async Task HandleAsync(PaymentRefundSettled integrationEvent, CancellationToken cancellationToken)
    {
        if (await cases.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        // The order first, then the tracked case (see DecideAsync): a detached case would stay Approved for ever while
        // this event was marked consumed.
        if (await cases.PeekAsync(integrationEvent.RefundId, cancellationToken) is { } peeked
            && await orders.FindAsync(peeked.OrderId, cancellationToken) is var order
            && await cases.FindAsync(integrationEvent.RefundId, cancellationToken) is { } refundCase)
        {
            if (refundCase.Settle(integrationEvent.Succeeded, now))
            {
                var outcome = integrationEvent.Succeeded ? "refunded" : "not refunded (the provider could not, or it was not possible)";
                var context = new TransitionContext(now, Actor, integrationEvent.CorrelationId);
                if (order is not null)
                {
                    order.NoteRefund(refundCase.CancelledItemIds, $"Refund case {refundCase.Id:N} {outcome}: {refundCase.Amount.Amount} {refundCase.Amount.Currency.Value}",
                        context, $"payment:{integrationEvent.PaymentId}");
                }

                orders.Audit(AuditEntry.For(new AuditSource(integrationEvent.CorrelationId, null, null), now, Actor, "refunds.settle",
                    $"refund-case:{refundCase.Id}", nameof(RefundCaseStatus.Approved), refundCase.Status.ToString()));
            }

            if (!integrationEvent.Succeeded)
            {
                LogRefundFailed(logger, refundCase.Id, refundCase.OrderId);
            }
        }
        else
        {
            LogUnknownRefund(logger, integrationEvent.RefundId, integrationEvent.OrderId);
        }

        cases.MarkConsumed(integrationEvent.EventId, Name, now);
        if (!await orders.TrySaveAsync(cancellationToken) && !await cases.HasConsumedAsync(integrationEvent.EventId, Name, cancellationToken))
        {
            throw new InvalidOperationException($"Refund case {integrationEvent.RefundId} changed concurrently.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "RefundOutcomeUnknownCase",
        Message = "A refund outcome for {RefundId} (order {OrderId}) names no refund case; it is ignored")]
    private static partial void LogUnknownRefund(ILogger logger, Guid refundId, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, EventName = "RefundCaseFailed",
        Message = "Alert: refund case {CaseId} for order {OrderId} was not refunded; a person follows it up")]
    private static partial void LogRefundFailed(ILogger logger, Guid caseId, Guid orderId);
}

/// <summary>
/// The Worker's watch over approved refunds (ADR 0027 §8): one not refunded within <c>Refunds:ExecutionTargetDays</c> of
/// its approval raises <c>RefundCaseOverdue</c> once (an outbox message given up on, a refund in review at Payments). It
/// changes nothing else.
/// </summary>
internal sealed partial class WatchRefundCasesJob(
    IRefundCaseStore cases, IOrderStore orders, IOptions<RefundOptions> options, TimeProvider timeProvider, ILogger<WatchRefundCasesJob> logger) : IBackgroundJob
{
    public const string Name = "orders.watch-refund-cases";
    public const int BatchSize = 50;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var overdue = await cases.FindOverdueAsync(now.AddDays(-options.Value.ExecutionTargetDays), BatchSize, cancellationToken);
        foreach (var refundCase in overdue)
        {
            refundCase.MarkOverdueAlerted(now);
            LogOverdue(logger, refundCase.Id, refundCase.OrderId, options.Value.ExecutionTargetDays);
        }

        return overdue.Count > 0 && await orders.TrySaveAsync(cancellationToken) ? overdue.Count : 0;
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "RefundCaseOverdue",
        Message = "Alert: refund case {CaseId} for order {OrderId} was approved more than {Days} days ago and is not refunded")]
    private static partial void LogOverdue(ILogger logger, Guid caseId, Guid orderId, int days);
}
