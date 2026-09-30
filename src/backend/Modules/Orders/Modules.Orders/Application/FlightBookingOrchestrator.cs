using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>
/// <c>Orders:BookingReconciliation</c> (booking-lifecycle.md). The windows are the widest any composed supplier needs;
/// a supplier's ADR records its own (ADR 0019 for Amadeus).
/// </summary>
internal sealed class BookingReconciliationOptions
{
    public const string SectionName = "Orders:BookingReconciliation";

    /// <summary>
    /// A booking still in Booking this long after it started was interrupted (a crash or a lost save): it is looked up,
    /// never sent again. Longer than any supplier booking call can take.
    /// </summary>
    public TimeSpan LookupAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after the booking started a lookup that finds nothing proves nothing was booked.</summary>
    public TimeSpan NotFoundConclusiveAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A booking still unknown this long after it started goes to a person (with an alert).</summary>
    public TimeSpan ManualReviewAfter { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Books an order's items with their suppliers and settles its payment (ADR 0005, ADR 0021): authorize → book → capture.
/// Each item is booked once, under its own id as our client reference, and only by the request that moved the order to
/// Booking. The outcome decides the item's state:
/// - Booked: Confirmed, with the supplier's locator and ticketing state;
/// - NotBooked: Failed (a definitive refusal);
/// - Unknown: PendingConfirmation, to be looked up by our reference, NEVER booked again;
/// - Mismatch: ManualReview, never charged.
/// Once every item is settled, the payment is charged for the confirmed items (never before a confirmed booking), or
/// released when nothing was booked, through the outbox in the same save (F-25). Reconciliation settles the unknown
/// ones by lookup: found → Confirmed; not found after the supplier's consistency window → Failed; unresolved after its
/// limit → ManualReview, with an alert.
/// </summary>
internal sealed partial class FlightBookingOrchestrator(
    IOrderStore store,
    IFlightBookings bookings,
    IOrderTravellers travellers,
    TimeProvider timeProvider,
    IOptions<BookingReconciliationOptions> options,
    ILogger<FlightBookingOrchestrator> logger)
{
    public const string ReconciliationActor = "system:booking-reconciliation";
    private const int _saveAttempts = 3;

    /// <summary>
    /// Books every item of an order this request just moved to Booking (and saved). The supplier calls happen first; their
    /// outcomes are then applied and saved, retried on a concurrent change (re-applying them is idempotent). If they still
    /// cannot be saved, the items stay Booking and reconciliation looks them up: nothing is ever sent twice.
    /// </summary>
    public async Task<Order> BookAsync(Order order, TransitionContext context, CancellationToken cancellationToken)
    {
        var items = order.Items.Where(i => i.Status is FlightOrderItemStatus.Booking).ToList();
        if (items.Count == 0)
        {
            return order;
        }

        var people = await travellers.GetForBookingAsync(order.Id, order.CustomerId, context.CorrelationId, cancellationToken);
        var outcomes = new Dictionary<Guid, FlightBookingResult>();
        foreach (var item in items)
        {
            outcomes[item.Id] = people is null
                ? new FlightBookingResult(FlightBookingStatus.NotBooked, Detail: "The travellers could not be read for the booking; nothing was sent to the supplier")
                : TooLateToSend(item)
                    // Reconciliation's windows count from the start of booking: a send this late could land after it
                    // concluded "not booked" and released the hold. Nothing is sent, so nothing is booked.
                    ? new FlightBookingResult(FlightBookingStatus.NotBooked, Detail: "The booking could not be sent in time; nothing was sent to the supplier")
                    : await BookItemAsync(order, item, people, context, cancellationToken);
        }

        return await ApplyAndSaveAsync(order, outcomes, context, reconciling: false, cancellationToken);
    }

    /// <summary>
    /// Looks up the order's unsettled bookings by our reference (a read, safe to repeat): items PendingConfirmation, and
    /// items left in Booking longer than <see cref="BookingReconciliationOptions.LookupAfter"/>. Never books.
    /// </summary>
    public async Task<bool> ReconcileAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (await store.FindAsync(orderId, cancellationToken) is not { } order)
        {
            return false;
        }

        var outcomes = new Dictionary<Guid, FlightBookingResult>();
        foreach (var item in order.Items.Where(i => NeedsLookup(i, now)))
        {
            outcomes[item.Id] = await bookings.ReconcileAsync(item.SelectedOfferId, order.CustomerId, item.Id.ToString(), item.AgreedPrice, cancellationToken);
        }

        if (outcomes.Count == 0)
        {
            return false;
        }

        // One correlation id per run for this order, so its timeline entries and the charge or release line up (observability).
        var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? $"reconcile-{Guid.NewGuid():N}";
        await ApplyAndSaveAsync(order, outcomes, new TransitionContext(now, ReconciliationActor, correlationId), reconciling: true, cancellationToken);
        return true;
    }

    // Well inside LookupAfter (and so NotFoundConclusiveAfter): a send is never in flight when reconciliation concludes.
    private bool TooLateToSend(FlightOrderItem item) =>
        item.BookingStartedAt is not { } started || timeProvider.GetUtcNow() - started >= options.Value.LookupAfter / 2;

    // An item from before booking start times were recorded was never sent (booking did not exist then): look it up now.
    private bool NeedsLookup(FlightOrderItem item, DateTimeOffset now) =>
        (item.NextBookingLookupAt is null || item.NextBookingLookupAt <= now)
        && (item.Status is FlightOrderItemStatus.PendingConfirmation
            || (item.Status is FlightOrderItemStatus.Booking && (item.BookingStartedAt is not { } started || now - started >= options.Value.LookupAfter)));

    private async Task<FlightBookingResult> BookItemAsync(Order order, FlightOrderItem item, BookingTravellers people, TransitionContext context, CancellationToken cancellationToken)
    {
        var request = new FlightBookingRequest(
            item.SelectedOfferId,
            order.CustomerId,
            item.Id.ToString(),
            item.AgreedPrice,
            [.. people.Travellers.Select(Passenger)],
            new FlightBookingContact(people.Email, people.Phone),
            context.CorrelationId);
        try
        {
            return await bookings.BookAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Flights classifies supplier failures itself; anything escaping it may still hide a booking: unknown.
            LogBookingCallFailed(logger, order.Id, item.Id, exception.GetType().Name);
            return new FlightBookingResult(FlightBookingStatus.Unknown);
        }
    }

    private async Task<Order> ApplyAndSaveAsync(
        Order order, IReadOnlyDictionary<Guid, FlightBookingResult> outcomes, TransitionContext context, bool reconciling, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            foreach (var (itemId, outcome) in outcomes)
            {
                Apply(order, itemId, outcome, context, reconciling);
            }

            Settle(order, context);
            if (await store.TrySaveAsync(cancellationToken))
            {
                return order;
            }

            // Changed at the same time (e.g. reconciliation of an interrupted booking): apply the same outcomes to the
            // current order. Transitions already made are no-ops.
            if (await store.FindAsync(order.Id, cancellationToken) is not { } current)
            {
                return order;
            }

            if (attempt == _saveAttempts)
            {
                LogOutcomeNotSaved(logger, order.Id);
                return current; // what is stored, never what was not saved; reconciliation looks it up (never books again)
            }

            order = current;
        }
    }

    private void Apply(Order order, Guid itemId, FlightBookingResult outcome, TransitionContext context, bool reconciling)
    {
        if (order.Items.SingleOrDefault(i => i.Id == itemId) is not { } item)
        {
            return;
        }

        // An item without a start time predates booking (nothing was ever sent): every window has passed.
        var sinceStart = item.BookingStartedAt is { } started ? context.At - started : TimeSpan.MaxValue;
        switch (outcome.Status)
        {
            case FlightBookingStatus.Booked when outcome is { ProviderId: { } providerId, Locator: { } locator }:
                var ticketing = outcome.Ticketing is FlightTicketingStatus.Issued ? TicketingStatus.Issued : TicketingStatus.Pending;
                if (!order.Confirm(itemId, providerId, locator, context, ticketing).IsSuccess && item.Status is FlightOrderItemStatus.Failed)
                {
                    // Found after it was concluded absent: its hold may be released already. A person must act now.
                    LogBookingFoundAfterFailure(logger, order.Id, itemId);
                }

                break;
            case FlightBookingStatus.NotBooked when !reconciling:
                order.Fail(itemId, outcome.Detail ?? "The supplier refused the booking; nothing was booked", context);
                break;
            case FlightBookingStatus.NotFound when reconciling && sinceStart >= options.Value.NotFoundConclusiveAfter:
                order.Fail(itemId, "The supplier has no booking under our reference after its consistency window; nothing was booked", context);
                break;
            case FlightBookingStatus.Mismatch:
                order.RequireManualReview(itemId, "A booking exists at the supplier but not as agreed; it is never charged until a person decides", context);
                LogMismatch(logger, order.Id, itemId);
                break;
            default:
                // Unknown, a NotFound still inside the window, or anything not conclusive: look it up later, never resubmit.
                order.AwaitConfirmation(itemId, "Booking outcome unknown; it is looked up by our reference, never sent again", context);
                break;
        }

        if (reconciling && item.Status is FlightOrderItemStatus.PendingConfirmation && sinceStart >= options.Value.ManualReviewAfter)
        {
            order.RequireManualReview(itemId, $"Booking still unknown {options.Value.ManualReviewAfter.TotalHours:0} hours after it started", context);
            LogUnresolved(logger, order.Id, itemId);
        }
        else if (reconciling && item.Status is FlightOrderItemStatus.PendingConfirmation)
        {
            // Look again later, backing off (30 s, 1, 2, 4 ... up to 30 minutes): a supplier outage costs few lookups.
            var delay = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, item.BookingLookups), TimeSpan.FromMinutes(30).TotalSeconds));
            order.RecordBookingLookup(itemId, context.At, context.At + delay);
        }
    }

    private void Settle(Order order, TransitionContext context)
    {
        switch (order.SettlePayment(context))
        {
            case PaymentSettlement.Capture capture:
                store.Publish(new OrderPaymentCaptureRequested(Guid.NewGuid(), context.At, order.Id, capture.PaymentId, capture.Amount, context.CorrelationId), context.CorrelationId);
                break;
            case PaymentSettlement.Release release:
                PaymentHolds.Publish(store, order.Id, release.PaymentId, "nothing was booked", context);
                break;
        }
    }

    private static FlightBookingPassenger Passenger(BookingTraveller traveller) => new(
        traveller.Type switch { TravellerType.Child => FlightPassengerKind.Child, TravellerType.Infant => FlightPassengerKind.Infant, _ => FlightPassengerKind.Adult },
        traveller.GivenNames,
        traveller.Surname,
        traveller.DateOfBirth,
        traveller.Gender is TravellerGenderType.Male ? FlightPassengerGender.Male : FlightPassengerGender.Female,
        traveller.Document is { } document
            ? new FlightBookingDocument(
                document.Kind is TravelDocumentKind.IdentityCard ? FlightTravelDocumentKind.IdentityCard : FlightTravelDocumentKind.Passport,
                document.Number, document.IssuingCountry, document.Nationality, document.ExpiryDate)
            : null);

    // Our ids only: never passenger data or supplier messages (security rules).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Booking call for order {OrderId} item {ItemId} failed ({ExceptionType}); outcome unknown, it will be looked up, never resubmitted")]
    private static partial void LogBookingCallFailed(ILogger logger, Guid orderId, Guid itemId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Booking outcomes for order {OrderId} could not be saved after concurrent changes; reconciliation will look them up")]
    private static partial void LogOutcomeNotSaved(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, EventName = "BookingMismatch", Message = "Alert: order {OrderId} item {ItemId} has a supplier booking not as agreed; manual review, not charged")]
    private static partial void LogMismatch(ILogger logger, Guid orderId, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, EventName = "BookingUnresolved", Message = "Alert: order {OrderId} item {ItemId} booking outcome still unknown after its limit; manual review")]
    private static partial void LogUnresolved(ILogger logger, Guid orderId, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, EventName = "BookingFoundAfterFailure", Message = "Alert: order {OrderId} item {ItemId} was found booked at the supplier after it was concluded not booked; manual action needed")]
    private static partial void LogBookingFoundAfterFailure(ILogger logger, Guid orderId, Guid itemId);
}

/// <summary>The booking reconciliation job: each order on the work list in its own scope, so one failure never blocks the rest.</summary>
internal sealed partial class ReconcileBookingsJob(
    IOrderStore store,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<BookingReconciliationOptions> options,
    ILogger<ReconcileBookingsJob> logger) : IBackgroundJob
{
    public const string Name = "orders.reconcile-bookings";
    public const int BatchSize = 50;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var handled = 0;
        foreach (var orderId in await store.FindBookingsToReconcileAsync(startedAt - options.Value.LookupAfter, startedAt, BatchSize, cancellationToken))
        {
            if (BackgroundJobRun.IsOver(startedAt, timeProvider))
            {
                break;
            }

            handled++;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FlightBookingOrchestrator>().ReconcileAsync(orderId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, orderId, exception.GetType().Name);
            }
        }

        return handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling the bookings of order {OrderId} failed ({Error}); it stays on the work list")]
    private static partial void LogFailed(ILogger logger, Guid orderId, string error);
}
