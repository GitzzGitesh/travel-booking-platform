using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Orders.Domain;

/// <summary>
/// What an order item books (ADR 0030 §6). One item lifecycle for every product: only the module that books it
/// (Flights or Hotels, through its Contracts) and the confirmation documents differ (tickets for a flight; none tracked
/// for a hotel, whose confirmation number is the voucher reference).
/// </summary>
internal enum OrderProduct
{
    Flight,
    Hotel,
}

/// <summary>
/// The flight order item lifecycle (booking-lifecycle.md, ADR 0005), up to the booking outcome. Fulfilment and
/// cancellation states come with their stories. Draft is transient: an item is only created from a selection Flights has
/// already revalidated, so price changes and expiry before ordering are handled there (F-01, F-02).
/// </summary>
internal enum FlightOrderItemStatus
{
    Draft,
    AwaitingPayment,
    Abandoned,
    Booking,
    PendingConfirmation,
    ManualReview,
    Confirmed,
    Failed,

    /// <summary>A confirmed booking cancelled at the supplier after the charge (ADR 0027): any refund follows its refund case.</summary>
    Cancelled,
}

/// <summary>Whether the supplier has issued the tickets for a confirmed item.</summary>
internal enum TicketingStatus
{
    Pending,
    Issued,
}

/// <summary>What the order's payment is to become once its bookings are settled (ADR 0005: authorize → book → capture).</summary>
internal abstract record PaymentSettlement(Guid PaymentId)
{
    /// <summary>Charge the confirmed items' agreed prices (a partial capture releases the rest).</summary>
    internal sealed record Capture(Guid PaymentId, Money Amount) : PaymentSettlement(PaymentId);

    /// <summary>Nothing was booked: release the hold (invariant 4: only once the booking is known not to exist).</summary>
    internal sealed record Release(Guid PaymentId) : PaymentSettlement(PaymentId);
}

/// <summary>Derived from the items, never stored or set directly (booking-lifecycle.md).</summary>
internal enum OrderStatus
{
    Draft,
    AwaitingPayment,
    Pending,
    Confirmed,
    PartiallyConfirmed,
    Failed,
    Abandoned,

    /// <summary>Every booked item was cancelled at the supplier afterwards (ADR 0027).</summary>
    Cancelled,
}

/// <summary>Who changed what, and when: recorded on every transition, with the request's correlation id.</summary>
internal sealed record TransitionContext(DateTimeOffset At, string Actor, string? CorrelationId = null);

/// <summary>
/// What the item needs from its travellers (Q9), snapshotted from Flights: the passenger mix, whether the supplier
/// requires travel documents, and the last travel date that dates their personal data's retention.
/// </summary>
internal sealed record TravellerNeeds(int Adults, int Children, int Infants, bool DocumentsRequired, DateOnly LastTravelDate)
{
    public bool IsKnown => Adults > 0;
}

/// <summary>
/// A hotel rate's cancellation terms as the customer agreed to them (ADR 0030 §7), snapshotted on the order item when it
/// is created and when it is paid: the refund on cancellation follows them, whatever the supplier's selection says later.
/// The penalty is in the item's currency.
/// </summary>
internal sealed record CancellationTerms(bool Refundable, DateTimeOffset? FreeUntil, decimal? PenaltyAmount)
{
    public static CancellationTerms NonRefundable { get; } = new(false, null, null);

    /// <summary>
    /// What the terms refund of <paramref name="paid"/> when the customer asked at <paramref name="askedAt"/>: all of it
    /// before the deadline; after it, the price less the penalty (the whole price when none is stated); nothing for a
    /// non-refundable rate. Never negative, never more than was paid.
    /// </summary>
    public decimal RefundOf(decimal paid, DateTimeOffset askedAt) =>
        !Refundable || FreeUntil is not { } deadline ? 0
        : askedAt < deadline ? paid
        : Math.Max(0, paid - Math.Min(paid, PenaltyAmount ?? paid));

    public string Describe(string currency) =>
        !Refundable || FreeUntil is null ? "non-refundable"
        : $"free cancellation until {FreeUntil:O}, then {(PenaltyAmount is { } p ? $"a {p} {currency} charge" : "no refund")}";
}

/// <summary>The customer's consent to a changed price (F-01), snapshotted from Flights when the order is created.</summary>
internal sealed record PriceConsent(Guid AcceptedPriceQuoteId, DateTimeOffset AcceptedAt);

internal abstract record OrderTransitionError
{
    private OrderTransitionError()
    {
    }

    internal sealed record ItemNotFound(Guid ItemId) : OrderTransitionError;

    internal sealed record Illegal(FlightOrderItemStatus From, FlightOrderItemStatus To) : OrderTransitionError;

    internal sealed record MissingReference(string Name) : OrderTransitionError;

    /// <summary>The transition already happened with a different reference: a conflicting event, not a replay.</summary>
    internal sealed record ConflictingReference(string Name) : OrderTransitionError;

    /// <summary>F-02: the supplier's offer for this item has expired; it cannot be booked.</summary>
    internal sealed record OfferExpired(Guid ItemId) : OrderTransitionError;

    /// <summary>F-01: a different price without new consent evidence (or in another currency) is never adopted.</summary>
    internal sealed record PriceNotAccepted(Guid ItemId) : OrderTransitionError;
}

/// <summary>
/// The customer's checkout (ADR 0005): order items with their own lifecycles, one payment for the order, and an
/// append-only timeline. The only way to change an item's status is a method here, and every change appends a timeline
/// entry. Re-applying a transition that already happened is a no-op (booking-lifecycle.md, invariant 6).
/// </summary>
internal sealed class Order
{
    private readonly List<FlightOrderItem> _items = [];
    private readonly List<OrderTimelineEntry> _timeline = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public const int MaxCustomerIdLength = 128;

    /// <summary>
    /// The signed-in customer who owns the order (Q8: sign-in is required before booking): the identity provider's
    /// opaque subject id. Every read and change is scoped to it (no IDOR).
    /// </summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>The creating request's idempotency key: unique per customer, so a replay returns this order (booking rules).</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Incremented by every change, so the order row is always rewritten and its rowversion guards item changes too
    /// (a timestamp alone can repeat, and then no concurrency check would run).
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>The payment authorization for the whole order (ADR 0005: payments attach to the Order).</summary>
    public string? PaymentAuthorizationId { get; private set; }

    /// <summary>When the charge (or, with nothing booked, the release) of the payment was requested: once per order.</summary>
    public DateTimeOffset? PaymentSettlementRequestedAt { get; private set; }

    public IReadOnlyList<FlightOrderItem> Items => _items;

    public IReadOnlyList<OrderTimelineEntry> Timeline => _timeline;

    public Money Total => _items.Select(i => i.AgreedPrice).Aggregate((sum, price) => sum + price);

    public OrderStatus Status => Derive(_items.Select(i => i.Status).ToList());

    /// <summary>
    /// An order for one confirmed flight selection. The price is the one Flights confirmed with the supplier and the
    /// customer agreed to, with the consent evidence when that price was an accepted change; the item is ready for
    /// payment (Draft → AwaitingPayment, both on the timeline).
    /// </summary>
    public static Order CreateForFlight(
        string customerId,
        string idempotencyKey, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context,
        TravellerNeeds? needs = null) =>
        Create(OrderProduct.Flight, customerId, idempotencyKey, selectedOfferId, agreedPrice, offerExpiresAt, consent, context, needs);

    /// <summary>An order for one confirmed hotel selection (ADR 0030): the same rules as <see cref="CreateForFlight"/>.</summary>
    public static Order CreateForHotel(
        string customerId,
        string idempotencyKey, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context,
        TravellerNeeds needs, CancellationTerms terms)
    {
        var order = Create(OrderProduct.Hotel, customerId, idempotencyKey, selectedOfferId, agreedPrice, offerExpiresAt, consent, context, needs);
        order._items[0].SetCancellationTerms(terms);
        return order;
    }

    private static Order Create(
        OrderProduct product,
        string customerId,
        string idempotencyKey, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context,
        TravellerNeeds? needs)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = IsValidCustomerId(customerId) ? customerId : throw new ArgumentException("A customer id is required.", nameof(customerId)),
            IdempotencyKey = idempotencyKey,
            CreatedAt = context.At,
            UpdatedAt = context.At,
        };
        var item = new FlightOrderItem(Guid.NewGuid(), selectedOfferId, agreedPrice, offerExpiresAt, consent, product);
        if (needs is not null)
        {
            item.SetTravellerNeeds(needs);
        }

        order._items.Add(item);
        var selection = product is OrderProduct.Hotel ? "hotel" : "flight";
        var reason = consent is null
            ? $"Order created from a confirmed {selection} selection"
            : $"Order created from a confirmed {selection} selection at an accepted changed price (quote {consent.AcceptedPriceQuoteId}, accepted {consent.AcceptedAt:O})";
        order.Record(item, null, reason, context, providerReference: null);
        order.Move(item, FlightOrderItemStatus.AwaitingPayment, "Price confirmed with the supplier", context, providerReference: null);
        return order;
    }

    /// <summary>
    /// The checkout ended with NO payment authorization outstanding: the offer expired, or the customer gave up. A card
    /// decline is not this: the item stays AwaitingPayment for another attempt (F-20); and an authorization with an
    /// unknown outcome (timeout) is resolved on the payment side before anything is abandoned.
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> Abandon(Guid itemId, string reason, TransitionContext context) =>
        Transition(itemId, FlightOrderItemStatus.Abandoned, reason, context, providerReference: null, FlightOrderItemStatus.AwaitingPayment);

    /// <summary>
    /// Adopts an item's terms from a fresh supplier revalidation, right before payment: the offer's new expiry and, when
    /// the customer accepted a changed price in Flights (F-01), that price with its consent evidence. A different price
    /// without a newly accepted quote is refused. Only while the item awaits payment; a price change is on the timeline.
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> RefreshOffer(
        Guid itemId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context, bool? documentsRequired = null,
        CancellationTerms? terms = null)
    {
        if (Find(itemId) is not { } item)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.ItemNotFound(itemId));
        }

        if (item.Status is not FlightOrderItemStatus.AwaitingPayment)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.Illegal(item.Status, FlightOrderItemStatus.AwaitingPayment));
        }

        // The supplier may state (or drop) a document requirement at a later revalidation: the latest one applies (Q9).
        if (documentsRequired is { } required && item.TravellerNeeds is { } needs && needs.DocumentsRequired != required)
        {
            item.SetTravellerNeeds(needs with { DocumentsRequired = required });
            Record(item, item.Status, required ? "The supplier now requires travel documents" : "The supplier no longer requires travel documents", context, providerReference: null);
        }

        var repriced = agreedPrice != item.AgreedPrice;
        if (repriced && (agreedPrice.Currency != item.AgreedPrice.Currency || consent is null || consent.AcceptedPriceQuoteId == item.AcceptedPriceQuoteId))
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.PriceNotAccepted(itemId));
        }

        // A hotel's terms as agreed right before payment (the supplier's selection is Confirmed only once the customer
        // accepted any change, F-53): what a cancellation is refunded by.
        if (terms is not null && item.Product is OrderProduct.Hotel && terms != item.CancellationTerms)
        {
            if (consent is null || (!repriced && consent.AcceptedPriceQuoteId == item.AcceptedPriceQuoteId))
            {
                return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.PriceNotAccepted(itemId));
            }

            item.SetCancellationTerms(terms);
            if (!repriced)
            {
                item.RecordConsent(consent); // a repricing records it below
            }

            Record(item, item.Status,
                $"Cancellation terms now {terms.Describe(item.AgreedPrice.Currency.Value)} (quote {consent.AcceptedPriceQuoteId}, accepted {consent.AcceptedAt:O})",
                context, providerReference: null);
        }

        if (!repriced && offerExpiresAt == item.OfferExpiresAt)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Success(item.Status); // nothing new
        }

        item.RefreshTerms(offerExpiresAt, repriced ? agreedPrice : null, repriced ? consent : null);
        if (repriced)
        {
            Record(item, item.Status, $"Agreed price changed to {agreedPrice.Amount} {agreedPrice.Currency.Value} (quote {consent!.AcceptedPriceQuoteId}, accepted {consent.AcceptedAt:O})", context, providerReference: null);
        }
        else
        {
            UpdatedAt = context.At;
            Revision++;
        }

        return Result<FlightOrderItemStatus, OrderTransitionError>.Success(item.Status);
    }

    /// <summary>
    /// The order's payment is authorized, so the supplier bookings may start (authorize → book → capture): every item
    /// awaiting payment moves to Booking, all or none. There is no path to Booking without the authorization, and none
    /// for an item whose supplier offer has expired (F-02).
    /// </summary>
    public Result<OrderStatus, OrderTransitionError> StartBooking(string paymentAuthorizationId, TransitionContext context)
    {
        if (string.IsNullOrWhiteSpace(paymentAuthorizationId))
        {
            return Result<OrderStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference(nameof(paymentAuthorizationId)));
        }

        if (PaymentAuthorizationId is { } existing)
        {
            return existing == paymentAuthorizationId
                ? Result<OrderStatus, OrderTransitionError>.Success(Status) // already applied: a no-op
                : Result<OrderStatus, OrderTransitionError>.Failure(new OrderTransitionError.ConflictingReference(nameof(paymentAuthorizationId)));
        }

        if (_items.FirstOrDefault(i => i.Status is not FlightOrderItemStatus.AwaitingPayment) is { } notReady)
        {
            return Result<OrderStatus, OrderTransitionError>.Failure(new OrderTransitionError.Illegal(notReady.Status, FlightOrderItemStatus.Booking));
        }

        if (_items.FirstOrDefault(i => i.OfferExpiresAt <= context.At) is { } expired)
        {
            return Result<OrderStatus, OrderTransitionError>.Failure(new OrderTransitionError.OfferExpired(expired.Id));
        }

        PaymentAuthorizationId = paymentAuthorizationId;
        foreach (var item in _items)
        {
            item.StartBooking(context.At);
            Move(item, FlightOrderItemStatus.Booking, "Payment authorized; booking with the supplier", context, paymentAuthorizationId);
        }

        return Result<OrderStatus, OrderTransitionError>.Success(Status);
    }

    /// <summary>
    /// Records on the timeline that a payment authorization exists that this order will not book on (its offer expired,
    /// or the held amount is not the order's total): the hold must be released (payment-lifecycle.md). No status changes.
    /// Idempotent per authorization.
    /// </summary>
    /// <returns>True if this call added the note (the caller then asks Payments to release the hold); false if it was there.</returns>
    public bool NoteUnusedPaymentHold(string paymentAuthorizationId, string reason, TransitionContext context)
    {
        if (_timeline.Any(e => e.ProviderReference == paymentAuthorizationId))
        {
            return false;
        }

        foreach (var item in _items)
        {
            Record(item, item.Status, $"Payment not used: {reason}. The hold must be released", context, paymentAuthorizationId);
        }

        return true;
    }

    /// <summary>
    /// Abandons every item still awaiting payment whose supplier offer has expired (F-02). The caller first makes sure no
    /// payment attempt for the order still holds, or may hold, funds. Returns how many items it abandoned; none again on a
    /// repeat.
    /// </summary>
    public int AbandonExpired(TransitionContext context)
    {
        var expired = _items.Where(i => i.Status is FlightOrderItemStatus.AwaitingPayment && i.OfferExpiresAt <= context.At).ToList();
        foreach (var item in expired)
        {
            Move(item, FlightOrderItemStatus.Abandoned, "The offer expired before payment", context, providerReference: null);
        }

        return expired.Count;
    }

    /// <summary>The supplier confirmed the booking at the agreed price, directly or found by reconciliation.</summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> Confirm(
        Guid itemId, string providerId, string supplierLocator, TransitionContext context, TicketingStatus? ticketing = TicketingStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(supplierLocator))
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference(nameof(supplierLocator)));
        }

        if (Find(itemId) is { Status: FlightOrderItemStatus.Confirmed } confirmed
            && (confirmed.ProviderId != providerId || confirmed.SupplierLocator != supplierLocator))
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.ConflictingReference(nameof(supplierLocator)));
        }

        var result = Transition(itemId, FlightOrderItemStatus.Confirmed, "Supplier booking confirmed", context, $"{providerId}:{supplierLocator}",
            FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation, FlightOrderItemStatus.ManualReview);
        if (result.IsSuccess)
        {
            Find(itemId)!.RecordSupplierBooking(providerId, supplierLocator, ticketing);
        }

        return result;
    }

    /// <summary>
    /// The supplier definitely has no booking: a definitive refusal while booking, or reconciliation confirming absence
    /// after the supplier's consistency window, or an operator's decision in manual review. Only now may payment be voided.
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> Fail(Guid itemId, string reason, TransitionContext context) =>
        Transition(itemId, FlightOrderItemStatus.Failed, reason, context, providerReference: null,
            FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation, FlightOrderItemStatus.ManualReview);

    /// <summary>The booking outcome is unknown (timeout, ambiguous error): reconcile by our reference, never resubmit.</summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> AwaitConfirmation(Guid itemId, string reason, TransitionContext context) =>
        Transition(itemId, FlightOrderItemStatus.PendingConfirmation, reason, context, providerReference: null, FlightOrderItemStatus.Booking);

    /// <summary>
    /// A person must decide: reconciliation is unresolved after its limit, or a booking exists but not as agreed
    /// (a supplier mismatch, found while booking or reconciling).
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> RequireManualReview(Guid itemId, string reason, TransitionContext context, string? providerReference = null) =>
        Transition(itemId, FlightOrderItemStatus.ManualReview, reason, context, providerReference,
            FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation);

    /// <summary>
    /// The item went to review because the supplier holds a booking that is not as agreed (recorded with its
    /// <c>provider:locator</c>). Such an item is never failed by a later "not found": a booking was seen under our
    /// reference, so its absence now proves nothing (a person decides).
    /// </summary>
    public bool HadSupplierMismatch(Guid itemId) =>
        _timeline.Any(e => e.ItemId == itemId && e.ToStatus == nameof(FlightOrderItemStatus.ManualReview) && e.ProviderReference is not null);

    /// <summary>The supplier booking last seen not as agreed for this item (its provider and locator), if any.</summary>
    public (string ProviderId, string Locator)? MismatchedBooking(Guid itemId) =>
        _timeline.LastOrDefault(e => e.ItemId == itemId && e.ToStatus == nameof(FlightOrderItemStatus.ManualReview)
                && e.ProviderReference is { } candidate && candidate.Contains(':', StringComparison.Ordinal) && !Guid.TryParse(candidate, out _))
            ?.ProviderReference is { } reference && reference.IndexOf(':', StringComparison.Ordinal) is > 0 and var separator && separator < reference.Length - 1
            ? (reference[..separator], reference[(separator + 1)..])
            : null;

    /// <summary>
    /// A confirmed booking cancelled at the supplier after the charge (ADR 0027), with the supplier desk's reference as
    /// evidence: recorded as a fact, before any refund is decided. Only a confirmed item.
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> CancelConfirmed(Guid itemId, string deskReference, TransitionContext context) =>
        string.IsNullOrWhiteSpace(deskReference)
            ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference(nameof(deskReference)))
            : Transition(itemId, FlightOrderItemStatus.Cancelled, "Cancelled at the supplier after the booking was confirmed", context, deskReference,
                FlightOrderItemStatus.Confirmed);

    /// <summary>
    /// Records a refund step on the timeline (ADR 0027: opened, decided, refunded or failed), on the cancelled items or, for
    /// a goodwill refund, the first item. Not a status change.
    /// </summary>
    public void NoteRefund(IReadOnlyCollection<Guid> itemIds, string reason, TransitionContext context, string? providerReference)
    {
        var items = itemIds.Count > 0 ? _items.Where(i => itemIds.Contains(i.Id)).ToList() : _items.Take(1).ToList();
        foreach (var item in items)
        {
            Record(item, item.Status, reason, context, providerReference);
        }
    }

    /// <summary>Whether any item of this order was ever in manual review (ADR 0027: its refunds always need a second person).</summary>
    public bool WasEverInReview => _timeline.Any(e => e.ToStatus == nameof(FlightOrderItemStatus.ManualReview));

    /// <summary>
    /// A staff outcome for an item in review (ADR 0025): the supplier booking was cancelled at the supplier's desk, with
    /// its reference as evidence. Nothing is booked any more, so nothing is charged for it (the hold follows the order's
    /// settlement).
    /// </summary>
    /// <remarks>
    /// Only for a booking that was seen under our reference (a mismatch): an item in review because its outcome is
    /// unknown leaves review only through a supplier lookup, never through a person's statement that releases the hold.
    /// </remarks>
    public Result<FlightOrderItemStatus, OrderTransitionError> CancelledAtSupplier(Guid itemId, string deskReference, TransitionContext context) =>
        string.IsNullOrWhiteSpace(deskReference)
            ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference(nameof(deskReference)))
            : MismatchedBooking(itemId) is null
            ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference("supplierBooking"))
            : Transition(itemId, FlightOrderItemStatus.Failed, "Cancelled at the supplier by operations; nothing is booked", context, deskReference,
                FlightOrderItemStatus.ManualReview);

    /// <summary>
    /// A staff outcome for an item in review (ADR 0025): the booking seen not as agreed is accepted, because a person
    /// checked that its travellers and flights are the customer's and its price is not above the agreed one. It is
    /// confirmed under that supplier reference, and the agreed price is charged, never more.
    /// </summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> AcceptAsBooked(Guid itemId, TransitionContext context) =>
        Find(itemId) is not { } item
            ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.ItemNotFound(itemId))
            : item.Status is not FlightOrderItemStatus.ManualReview
            ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.Illegal(item.Status, FlightOrderItemStatus.Confirmed))
            : MismatchedBooking(itemId) is not { } booking
                ? Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.MissingReference("supplierBooking"))
                : Confirm(itemId, booking.ProviderId, booking.Locator, context);

    /// <summary>
    /// Once every item's booking is settled (none booking, pending or in review): the charge for the confirmed items, or,
    /// with none confirmed, the release of the hold. Once per order (null again afterwards, or while anything is
    /// unsettled). The caller publishes it in the same save, so it is never lost (F-25). Recorded on the timeline with the
    /// payment as the provider reference. Capture only ever follows a confirmed booking (booking rules).
    /// </summary>
    public PaymentSettlement? SettlePayment(TransitionContext context)
    {
        if (PaymentAuthorizationId is null || PaymentSettlementRequestedAt is not null || !Guid.TryParse(PaymentAuthorizationId, out var paymentId)
            || _items.Any(i => i.Status is FlightOrderItemStatus.Booking or FlightOrderItemStatus.PendingConfirmation or FlightOrderItemStatus.ManualReview))
        {
            return null;
        }

        PaymentSettlementRequestedAt = context.At;
        var confirmed = _items.Where(i => i.Status is FlightOrderItemStatus.Confirmed).ToList();
        if (confirmed.Count == 0)
        {
            foreach (var item in _items)
            {
                Record(item, item.Status, "Nothing was booked: the payment hold is to be released", context, PaymentAuthorizationId);
            }

            return new PaymentSettlement.Release(paymentId);
        }

        var amount = confirmed.Select(i => i.AgreedPrice).Aggregate((sum, price) => sum + price);
        foreach (var item in confirmed)
        {
            Record(item, item.Status, $"Booking confirmed: {amount.Amount} {amount.Currency.Value} to be charged", context, PaymentAuthorizationId);
        }

        return new PaymentSettlement.Capture(paymentId, amount);
    }

    /// <summary>
    /// Records that reconciliation looked the item's booking up without settling it, and when to look again (backoff): not
    /// a status change, so no timeline entry.
    /// </summary>
    public void RecordBookingLookup(Guid itemId, DateTimeOffset at, DateTimeOffset nextLookupAt)
    {
        if (Find(itemId) is { } item)
        {
            item.RecordLookup(nextLookupAt);
            UpdatedAt = at;
            Revision++;
        }
    }

    /// <summary>Records a check of an item in manual review that did not settle it (no status change).</summary>
    public void NoteReviewCheck(Guid itemId, string reason, TransitionContext context)
    {
        if (Find(itemId) is { Status: FlightOrderItemStatus.ManualReview } item)
        {
            Record(item, item.Status, reason, context, providerReference: null);
        }
    }

    public static bool IsValidCustomerId(string? customerId) =>
        !string.IsNullOrWhiteSpace(customerId) && customerId.Length <= MaxCustomerIdLength;

    internal static OrderStatus Derive(IReadOnlyList<FlightOrderItemStatus> items)
    {
        if (items.Any(s => s is FlightOrderItemStatus.Booking or FlightOrderItemStatus.PendingConfirmation or FlightOrderItemStatus.ManualReview))
        {
            return OrderStatus.Pending;
        }

        if (items.All(s => s is FlightOrderItemStatus.Confirmed))
        {
            return OrderStatus.Confirmed;
        }

        if (items.All(s => s is FlightOrderItemStatus.Failed))
        {
            return OrderStatus.Failed;
        }

        if (items.Any(s => s is FlightOrderItemStatus.Cancelled) && items.All(s => s is FlightOrderItemStatus.Cancelled or FlightOrderItemStatus.Failed))
        {
            return OrderStatus.Cancelled;
        }

        if (items.Any(s => s is FlightOrderItemStatus.Confirmed)
            && items.All(s => s is FlightOrderItemStatus.Confirmed or FlightOrderItemStatus.Failed or FlightOrderItemStatus.Cancelled))
        {
            return OrderStatus.PartiallyConfirmed;
        }

        if (items.All(s => s is FlightOrderItemStatus.Abandoned))
        {
            return OrderStatus.Abandoned;
        }

        return items.Any(s => s is FlightOrderItemStatus.AwaitingPayment) ? OrderStatus.AwaitingPayment : OrderStatus.Draft;
    }

    private Result<FlightOrderItemStatus, OrderTransitionError> Transition(
        Guid itemId, FlightOrderItemStatus to, string reason, TransitionContext context, string? providerReference, params FlightOrderItemStatus[] allowedFrom)
    {
        if (Find(itemId) is not { } item)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.ItemNotFound(itemId));
        }

        if (item.Status == to)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Success(to); // already applied: a no-op
        }

        if (!allowedFrom.Contains(item.Status))
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.Illegal(item.Status, to));
        }

        Move(item, to, reason, context, providerReference);
        return Result<FlightOrderItemStatus, OrderTransitionError>.Success(to);
    }

    private void Move(FlightOrderItem item, FlightOrderItemStatus to, string reason, TransitionContext context, string? providerReference)
    {
        var from = item.Status;
        item.MoveTo(to);
        Record(item, from, reason, context, providerReference);
    }

    private void Record(FlightOrderItem item, FlightOrderItemStatus? from, string reason, TransitionContext context, string? providerReference)
    {
        _timeline.Add(new OrderTimelineEntry(Id, item.Id, context.At, context.Actor, from?.ToString(), item.Status.ToString(), reason, context.CorrelationId, providerReference));
        UpdatedAt = context.At;
        Revision++;
    }

    private FlightOrderItem? Find(Guid itemId) => _items.SingleOrDefault(i => i.Id == itemId);
}

/// <summary>
/// One flight booking in an order. Its <see cref="Id"/> is our client reference at the supplier and the basis of the
/// capture idempotency key (ADR 0005). Status changes only through <see cref="Order"/>.
/// </summary>
internal sealed class FlightOrderItem
{
    private FlightOrderItem()
    {
    }

    internal FlightOrderItem(Guid id, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, OrderProduct product = OrderProduct.Flight)
    {
        Id = id;
        Product = product;
        SelectedOfferId = selectedOfferId;
        AgreedPrice = agreedPrice;
        OfferExpiresAt = offerExpiresAt;
        AcceptedPriceQuoteId = consent?.AcceptedPriceQuoteId;
        PriceAcceptedAt = consent?.AcceptedAt;
        Status = FlightOrderItemStatus.Draft;
    }

    public Guid Id { get; private set; }

    /// <summary>What the item books: a flight (the default, for items created before hotels) or a hotel stay.</summary>
    public OrderProduct Product { get; private set; }

    /// <summary>The Flights or Hotels selection this item books (at most one order item per selection).</summary>
    public Guid SelectedOfferId { get; private set; }

    /// <summary>The supplier-confirmed price the customer agreed to: the amount to authorize and, once booked, capture.</summary>
    public Money AgreedPrice { get; private set; }

    public DateTimeOffset OfferExpiresAt { get; private set; }

    /// <summary>Consent evidence (F-01): the accepted price quote, when the agreed price was an accepted change.</summary>
    public Guid? AcceptedPriceQuoteId { get; private set; }

    public DateTimeOffset? PriceAcceptedAt { get; private set; }

    public FlightOrderItemStatus Status { get; private set; }

    public string? ProviderId { get; private set; }

    /// <summary>The supplier's booking locator (PNR or order id), once confirmed.</summary>
    public string? SupplierLocator { get; private set; }

    /// <summary>Whether the supplier has issued the tickets, once a flight is confirmed; null for a hotel.</summary>
    public TicketingStatus? Ticketing { get; private set; }

    /// <summary>
    /// When the order moved to Booking: the supplier may have received the booking from then on, so it dates the
    /// supplier's consistency window and the limit before a person must look (booking reconciliation).
    /// </summary>
    public DateTimeOffset? BookingStartedAt { get; private set; }

    /// <summary>What travellers the item needs (Q9); null for items created before it was recorded.</summary>
    public TravellerNeeds? TravellerNeeds { get; private set; }

    internal void SetTravellerNeeds(TravellerNeeds needs) => TravellerNeeds = needs;

    /// <summary>A hotel rate's agreed cancellation terms (ADR 0030 §7); null for a flight.</summary>
    public CancellationTerms? CancellationTerms { get; private set; }

    internal void SetCancellationTerms(CancellationTerms terms) => CancellationTerms = terms;

    internal void MoveTo(FlightOrderItemStatus status) => Status = status;

    internal void RefreshTerms(DateTimeOffset offerExpiresAt, Money? agreedPrice, PriceConsent? consent)
    {
        OfferExpiresAt = offerExpiresAt;
        if (agreedPrice is { } price && consent is not null)
        {
            AgreedPrice = price;
            AcceptedPriceQuoteId = consent.AcceptedPriceQuoteId;
            PriceAcceptedAt = consent.AcceptedAt;
        }
    }

    internal void RecordConsent(PriceConsent consent)
    {
        AcceptedPriceQuoteId = consent.AcceptedPriceQuoteId;
        PriceAcceptedAt = consent.AcceptedAt;
    }

    internal void RecordSupplierBooking(string providerId, string supplierLocator, TicketingStatus? ticketing)
    {
        ProviderId = providerId;
        SupplierLocator = supplierLocator;
        Ticketing = ticketing;
    }

    internal void StartBooking(DateTimeOffset at) => BookingStartedAt = at;

    /// <summary>How many lookups did not settle the booking, and when reconciliation looks again.</summary>
    public int BookingLookups { get; private set; }

    public DateTimeOffset? NextBookingLookupAt { get; private set; }

    internal void RecordLookup(DateTimeOffset nextLookupAt)
    {
        BookingLookups++;
        NextBookingLookupAt = nextLookupAt;
    }
}

/// <summary>
/// An append-only record of one transition (booking-lifecycle.md, invariant 1): actor, time, from → to, reason,
/// correlation id and provider reference (a payment authorization or "provider:locator"). Never updated or deleted.
/// </summary>
internal sealed record OrderTimelineEntry(
    Guid OrderId,
    Guid? ItemId,
    DateTimeOffset At,
    string Actor,
    string? FromStatus,
    string ToStatus,
    string Reason,
    string? CorrelationId,
    string? ProviderReference)
{
    public long Id { get; private set; }
}
