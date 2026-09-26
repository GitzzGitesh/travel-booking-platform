using TravelBooking.BuildingBlocks;

namespace TravelBooking.Modules.Orders.Domain;

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
}

/// <summary>Who changed what, and when: recorded on every transition, with the request's correlation id.</summary>
internal sealed record TransitionContext(DateTimeOffset At, string Actor, string? CorrelationId = null);

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
        string idempotencyKey, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = IsValidCustomerId(customerId) ? customerId : throw new ArgumentException("A customer id is required.", nameof(customerId)),
            IdempotencyKey = idempotencyKey,
            CreatedAt = context.At,
            UpdatedAt = context.At,
        };
        var item = new FlightOrderItem(Guid.NewGuid(), selectedOfferId, agreedPrice, offerExpiresAt, consent);
        order._items.Add(item);
        var reason = consent is null
            ? "Order created from a confirmed flight selection"
            : $"Order created from a confirmed flight selection at an accepted changed price (quote {consent.AcceptedPriceQuoteId}, accepted {consent.AcceptedAt:O})";
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
        Guid itemId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent, TransitionContext context)
    {
        if (Find(itemId) is not { } item)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.ItemNotFound(itemId));
        }

        if (item.Status is not FlightOrderItemStatus.AwaitingPayment)
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.Illegal(item.Status, FlightOrderItemStatus.AwaitingPayment));
        }

        var repriced = agreedPrice != item.AgreedPrice;
        if (repriced && (agreedPrice.Currency != item.AgreedPrice.Currency || consent is null || consent.AcceptedPriceQuoteId == item.AcceptedPriceQuoteId))
        {
            return Result<FlightOrderItemStatus, OrderTransitionError>.Failure(new OrderTransitionError.PriceNotAccepted(itemId));
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
            Move(item, FlightOrderItemStatus.Booking, "Payment authorized; booking with the supplier", context, paymentAuthorizationId);
        }

        return Result<OrderStatus, OrderTransitionError>.Success(Status);
    }

    /// <summary>The supplier confirmed the booking at the agreed price, directly or found by reconciliation.</summary>
    public Result<FlightOrderItemStatus, OrderTransitionError> Confirm(Guid itemId, string providerId, string supplierLocator, TransitionContext context)
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
            Find(itemId)!.RecordSupplierBooking(providerId, supplierLocator);
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
    public Result<FlightOrderItemStatus, OrderTransitionError> RequireManualReview(Guid itemId, string reason, TransitionContext context) =>
        Transition(itemId, FlightOrderItemStatus.ManualReview, reason, context, providerReference: null,
            FlightOrderItemStatus.Booking, FlightOrderItemStatus.PendingConfirmation);

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

        if (items.Any(s => s is FlightOrderItemStatus.Confirmed) && items.All(s => s is FlightOrderItemStatus.Confirmed or FlightOrderItemStatus.Failed))
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

    internal FlightOrderItem(Guid id, Guid selectedOfferId, Money agreedPrice, DateTimeOffset offerExpiresAt, PriceConsent? consent)
    {
        Id = id;
        SelectedOfferId = selectedOfferId;
        AgreedPrice = agreedPrice;
        OfferExpiresAt = offerExpiresAt;
        AcceptedPriceQuoteId = consent?.AcceptedPriceQuoteId;
        PriceAcceptedAt = consent?.AcceptedAt;
        Status = FlightOrderItemStatus.Draft;
    }

    public Guid Id { get; private set; }

    /// <summary>The Flights selection this item books (at most one order item per selection).</summary>
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

    internal void RecordSupplierBooking(string providerId, string supplierLocator)
    {
        ProviderId = providerId;
        SupplierLocator = supplierLocator;
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
