using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Hotels.Contracts;
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

/// <summary>A booking outcome, whatever the product (ADR 0030 §6): each product's Contracts result, mapped.</summary>
internal enum ItemBookingStatus
{
    Booked,
    NotBooked,
    Unknown,
    NotFound,
    Mismatch,
}

/// <param name="Ticketing">For a booked flight: whether its tickets are issued. Null for a hotel stay.</param>
internal sealed record ItemBookingOutcome(ItemBookingStatus Status, string? ProviderId = null, string? Locator = null, TicketingStatus? Ticketing = null, string? Detail = null);

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
/// limit → ManualReview, with an alert. Each item is booked and looked up through its own product's Contracts (Flights
/// or Hotels, ADR 0030 §6): never another module, never a fallback between them.
/// </summary>
internal sealed partial class FlightBookingOrchestrator(
    IOrderStore store,
    IFlightBookings flights,
    IHotelBookings hotels,
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
        var outcomes = new Dictionary<Guid, ItemBookingOutcome>();
        foreach (var item in items)
        {
            outcomes[item.Id] = people is null
                ? new ItemBookingOutcome(ItemBookingStatus.NotBooked, Detail: "The travellers could not be read for the booking; nothing was sent to the supplier")
                : TooLateToSend(item)
                    // Reconciliation's windows count from the start of booking: a send this late could land after it
                    // concluded "not booked" and released the hold. Nothing is sent, so nothing is booked.
                    ? new ItemBookingOutcome(ItemBookingStatus.NotBooked, Detail: "The booking could not be sent in time; nothing was sent to the supplier")
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

        var outcomes = new Dictionary<Guid, ItemBookingOutcome>();
        foreach (var item in order.Items.Where(i => NeedsLookup(i, now)))
        {
            outcomes[item.Id] = await LookupAsync(order, item, cancellationToken);
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

    /// <summary>
    /// A staff member's check of an item in manual review: the booking is looked up by our reference (never booked).
    /// Found as agreed → Confirmed; absent after the supplier's consistency window → Failed; anything else stays in
    /// review with the check on the timeline. The payment settles once nothing is unsettled (same save, by the caller).
    /// </summary>
    public async Task<FlightOrderItemStatus> CheckReviewAsync(Order order, Guid itemId, string reason, TransitionContext context, CancellationToken cancellationToken)
    {
        var item = order.Items.Single(i => i.Id == itemId);
        ItemBookingOutcome outcome;
        try
        {
            outcome = await LookupAsync(order, item, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReviewLookupFailed(logger, order.Id, itemId, exception.GetType().Name);
            outcome = new ItemBookingOutcome(ItemBookingStatus.Unknown); // the lookup failed: nothing is concluded
        }

        // Items whose booking start was never recorded predate booking (nothing was sent then): every window has passed.
        var sinceStart = item.BookingStartedAt is { } started ? context.At - started : TimeSpan.MaxValue;
        switch (outcome.Status)
        {
            case ItemBookingStatus.Booked when outcome is { ProviderId: { } providerId, Locator: { } locator }:
                order.Confirm(itemId, providerId, locator, context, outcome.Ticketing);
                break;
            case ItemBookingStatus.NotFound when sinceStart >= options.Value.NotFoundConclusiveAfter && !order.HadSupplierMismatch(itemId):
                order.Fail(itemId, $"Checked with the supplier: no booking under our reference; nothing was booked ({reason})", context);
                break;
            default:
                // Not as agreed, unknown, too early, or a mismatched booking no longer found: a person decides (never charged).
                order.NoteReviewCheck(itemId, $"Checked with the supplier ({outcome.Status}); still in review ({reason})", context);
                break;
        }

        Settle(order, context);
        return item.Status;
    }

    /// <summary>After a staff outcome (ADR 0025): the payment follows the order (charge, release, or nothing yet), in the same save.</summary>
    public void SettleAfterReviewOutcome(Order order, TransitionContext context) => Settle(order, context);

    // An item from before booking start times were recorded was never sent (booking did not exist then): look it up now.
    private bool NeedsLookup(FlightOrderItem item, DateTimeOffset now) =>
        (item.NextBookingLookupAt is null || item.NextBookingLookupAt <= now)
        && (item.Status is FlightOrderItemStatus.PendingConfirmation
            || (item.Status is FlightOrderItemStatus.Booking && (item.BookingStartedAt is not { } started || now - started >= options.Value.LookupAfter)));

    private async Task<ItemBookingOutcome> BookItemAsync(Order order, FlightOrderItem item, BookingTravellers people, TransitionContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Product is OrderProduct.Hotel)
            {
                // The occupancy the room was priced for: each guest's age at check-out (the needs' last travel date) from
                // their date of birth, 18 or over an adult; the lead guest (an adult) first, as hotels require.
                var checkOut = item.TravellerNeeds?.LastTravelDate ?? DateOnly.FromDateTime(context.At.UtcDateTime);
                var guests = people.Travellers
                    .Select(t => new HotelBookingGuest(t.GivenNames, t.Surname, AgeOn(t.DateOfBirth, checkOut) is var age and < 18 ? age : null))
                    .OrderBy(g => g.IsAdult ? 0 : 1);
                return Map(await hotels.BookAsync(
                    new HotelBookingRequest(
                        item.SelectedOfferId,
                        order.CustomerId,
                        item.Id.ToString(),
                        item.AgreedPrice,
                        [.. guests],
                        new HotelBookingContact(people.Email, people.Phone),
                        context.CorrelationId),
                    cancellationToken));
            }

            return Map(await flights.BookAsync(
                new FlightBookingRequest(
                    item.SelectedOfferId,
                    order.CustomerId,
                    item.Id.ToString(),
                    item.AgreedPrice,
                    [.. people.Travellers.Select(Passenger)],
                    new FlightBookingContact(people.Email, people.Phone),
                    context.CorrelationId),
                cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Flights and Hotels classify supplier failures themselves; anything escaping them may still hide a booking: unknown.
            LogBookingCallFailed(logger, order.Id, item.Id, exception.GetType().Name);
            return new ItemBookingOutcome(ItemBookingStatus.Unknown);
        }
    }

    private static int AgeOn(DateOnly dateOfBirth, DateOnly on)
    {
        var age = on.Year - dateOfBirth.Year;
        return dateOfBirth.AddYears(age) > on ? age - 1 : age;
    }

    // A lookup by our reference (the item id), with the product's own module. A read: safe to repeat.
    private async Task<ItemBookingOutcome> LookupAsync(Order order, FlightOrderItem item, CancellationToken cancellationToken) =>
        item.Product is OrderProduct.Hotel
            ? Map(await hotels.ReconcileAsync(item.SelectedOfferId, order.CustomerId, item.Id.ToString(), item.AgreedPrice, cancellationToken))
            : Map(await flights.ReconcileAsync(item.SelectedOfferId, order.CustomerId, item.Id.ToString(), item.AgreedPrice, cancellationToken));

    private static ItemBookingOutcome Map(FlightBookingResult result) => new(
        result.Status switch
        {
            FlightBookingStatus.Booked => ItemBookingStatus.Booked,
            FlightBookingStatus.NotBooked => ItemBookingStatus.NotBooked,
            FlightBookingStatus.NotFound => ItemBookingStatus.NotFound,
            FlightBookingStatus.Mismatch => ItemBookingStatus.Mismatch,
            _ => ItemBookingStatus.Unknown,
        },
        result.ProviderId,
        result.Locator,
        result.Ticketing is FlightTicketingStatus.Issued ? TicketingStatus.Issued : TicketingStatus.Pending,
        result.Detail);

    private static ItemBookingOutcome Map(HotelBookingResult result) => new(
        result.Status switch
        {
            HotelBookingStatus.Booked => ItemBookingStatus.Booked,
            HotelBookingStatus.NotBooked => ItemBookingStatus.NotBooked,
            HotelBookingStatus.NotFound => ItemBookingStatus.NotFound,
            HotelBookingStatus.Mismatch => ItemBookingStatus.Mismatch,
            _ => ItemBookingStatus.Unknown,
        },
        result.ProviderId,
        result.ConfirmationNumber,
        Ticketing: null,
        result.Detail);

    private async Task<Order> ApplyAndSaveAsync(
        Order order, IReadOnlyDictionary<Guid, ItemBookingOutcome> outcomes, TransitionContext context, bool reconciling, CancellationToken cancellationToken)
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

    private void Apply(Order order, Guid itemId, ItemBookingOutcome outcome, TransitionContext context, bool reconciling)
    {
        if (order.Items.SingleOrDefault(i => i.Id == itemId) is not { } item)
        {
            return;
        }

        // An item without a start time predates booking (nothing was ever sent): every window has passed.
        var sinceStart = item.BookingStartedAt is { } started ? context.At - started : TimeSpan.MaxValue;
        switch (outcome.Status)
        {
            case ItemBookingStatus.Booked when outcome is { ProviderId: { } providerId, Locator: { } locator }:
                if (!order.Confirm(itemId, providerId, locator, context, outcome.Ticketing).IsSuccess && item.Status is FlightOrderItemStatus.Failed)
                {
                    // Found after it was concluded absent: its hold may be released already. A person must act now.
                    LogBookingFoundAfterFailure(logger, order.Id, itemId);
                }

                break;
            case ItemBookingStatus.NotBooked when !reconciling:
                order.Fail(itemId, outcome.Detail ?? "The supplier refused the booking; nothing was booked", context);
                break;
            case ItemBookingStatus.NotFound when reconciling && sinceStart >= options.Value.NotFoundConclusiveAfter:
                order.Fail(itemId, "The supplier has no booking under our reference after its consistency window; nothing was booked", context);
                break;
            case ItemBookingStatus.Mismatch:
                order.RequireManualReview(itemId, "A booking exists at the supplier but not as agreed; it is never charged until a person decides", context,
                    outcome is { ProviderId: { } mismatchProvider, Locator: { } mismatchLocator } ? $"{mismatchProvider}:{mismatchLocator}" : null);
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
                // The customer's notice, in the same save as the charge (ADR 0024): exactly once per settlement.
                store.Publish(new OrderBookingSettled(Guid.NewGuid(), context.At, order.Id,
                    order.Status is OrderStatus.Confirmed ? BookingOutcome.Confirmed : BookingOutcome.PartiallyConfirmed,
                    [.. order.Items.Where(i => i.Status is FlightOrderItemStatus.Confirmed && i.SupplierLocator is not null).Select(i => i.SupplierLocator!)],
                    capture.Amount, context.CorrelationId), context.CorrelationId);
                break;
            case PaymentSettlement.Release release:
                PaymentHolds.Publish(store, order.Id, release.PaymentId, "nothing was booked", context);
                store.Publish(new OrderBookingSettled(Guid.NewGuid(), context.At, order.Id, BookingOutcome.NotBooked, [], null, context.CorrelationId), context.CorrelationId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The review check of order {OrderId} item {ItemId} could not look the booking up ({ExceptionType}); it stays in review")]
    private static partial void LogReviewLookupFailed(ILogger logger, Guid orderId, Guid itemId, string exceptionType);

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
