using System.Text;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Customers.Contracts;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Domain;
using TravelBooking.Modules.Payments.Contracts;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>
/// <paramref name="CustomerId"/> is the authenticated customer (Q8). <paramref name="IdempotencyKey"/> identifies this
/// payment attempt: the same key repeats it, a new key (after a decline) starts another.
/// </summary>
internal sealed record AuthorizeCheckout(Guid OrderId, string CustomerId, string IdempotencyKey, string PaymentMethodToken, string? CorrelationId)
{
    // Never printed: a card number pasted into the token field must not reach a log; the customer id is personal data.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"OrderId = {OrderId}, IdempotencyKey = {IdempotencyKey}, PaymentMethodToken = [redacted], CorrelationId = {CorrelationId}");
        return true;
    }
}

internal enum CheckoutStatus
{
    /// <summary>The supplier confirmed the booking (every item, or some: partially confirmed); only those are charged.</summary>
    Booked,

    /// <summary>The booking's outcome is not known yet: it is looked up (never sent again). Poll the order (202).</summary>
    BookingPending,

    /// <summary>The supplier did not book: nothing is charged, and the payment hold is released.</summary>
    BookingFailed,

    /// <summary>The customer must complete a challenge, then repeat the request with the same key.</summary>
    ActionRequired,

    /// <summary>Nothing held; the order still awaits payment. Another attempt needs a new key (F-20).</summary>
    Declined,

    /// <summary>The payment outcome is not known yet: repeat with the same key. Never booked on (F-24).</summary>
    PaymentPending,

    /// <summary>Nothing held (cancelled, expired, or confirmed absent); the order still awaits payment.</summary>
    PaymentFailed,

    /// <summary>The payment provider reported something unexpected: a person looks at it before anything else happens.</summary>
    PaymentManualReview,
}

internal sealed record CheckoutResult(Guid OrderId, CheckoutStatus Status, Guid? PaymentId, string? CustomerAction = null)
{
    /// <summary>What a customer may be told (generic declines): never why a card was refused, never a manual review.</summary>
    public CustomerPaymentState CustomerState => Status switch
    {
        CheckoutStatus.Booked or CheckoutStatus.BookingPending => CustomerPaymentState.Accepted,
        CheckoutStatus.BookingFailed => CustomerPaymentState.Released,
        CheckoutStatus.ActionRequired => CustomerPaymentState.ActionRequired,
        CheckoutStatus.Declined or CheckoutStatus.PaymentFailed => CustomerPaymentState.Declined,
        CheckoutStatus.PaymentPending => CustomerPaymentState.Pending,
        CheckoutStatus.PaymentManualReview => CustomerPaymentState.Unavailable,
        _ => throw new InvalidOperationException($"Unmapped checkout status {Status}."),
    };

    // Never printed: the customer action is a live client secret.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"OrderId = {OrderId}, Status = {Status}, PaymentId = {PaymentId}, CustomerAction = {(CustomerAction is null ? "null" : "[redacted]")}");
        return true;
    }
}

/// <summary>
/// The only payment states a customer-facing response may show (security rules: generic declines). The reason for a
/// decline, provider codes and a manual review stay in operations data (the payment attempt and its history).
/// </summary>
internal enum CustomerPaymentState
{
    /// <summary>The payment is held for the booking (charged once the booking is confirmed).</summary>
    Accepted,

    /// <summary>Nothing will be charged: the booking was not made and the hold is being released.</summary>
    Released,

    /// <summary>Complete the challenge (e.g. 3-D Secure), then repeat the request with the same key.</summary>
    ActionRequired,

    /// <summary>The payment did not go through: nothing is held. Use another payment method (a new key).</summary>
    Declined,

    /// <summary>Not known yet: repeat the request with the same key later.</summary>
    Pending,

    /// <summary>Payment cannot be completed right now: contact support. (A manual review, never named as one.)</summary>
    Unavailable,
}

internal abstract record CheckoutFailure
{
    private CheckoutFailure()
    {
    }

    internal sealed record InvalidRequest : CheckoutFailure;

    /// <summary>No such order for this customer (another customer's order is reported the same way: no IDOR).</summary>
    internal sealed record NotFound : CheckoutFailure;

    /// <summary>The order is past payment (booking, confirmed, abandoned...) under another payment, or not payable.</summary>
    internal sealed record NotAwaitingPayment(OrderStatus Status) : CheckoutFailure;

    /// <summary>F-01: the supplier's price changed. The customer accepts the new quote on the selection, then retries.</summary>
    internal sealed record PriceChanged(Guid ItemId) : CheckoutFailure;

    /// <summary>F-02: the offer expired, or would expire before the booking could start. Search again.</summary>
    internal sealed record OfferExpired(Guid ItemId) : CheckoutFailure;

    /// <summary>F-03: search again.</summary>
    internal sealed record SoldOut(Guid ItemId) : CheckoutFailure;

    /// <summary>The supplier could not answer, or the order changed at the same time: nothing was charged; retry.</summary>
    internal sealed record TryAgain : CheckoutFailure;

    /// <summary>The offer's supplier cannot book through us (Q6: a search-only adapter): nothing is charged; search again.</summary>
    internal sealed record SupplierCannotBook(Guid ItemId) : CheckoutFailure;

    /// <summary>The key was already used for this order with another amount (409: never a second effect).</summary>
    internal sealed record IdempotencyKeyReused : CheckoutFailure;

    internal sealed record InvalidPaymentMethod : CheckoutFailure;

    /// <summary>F-32: another payment attempt for this order (another key) may still hold funds; finish that one first.</summary>
    internal sealed record PaymentInProgress : CheckoutFailure;

    /// <summary>
    /// The travellers (names, dates of birth, genders, one per passenger), the booker's contact, and the travel documents
    /// when the supplier requires them, are not all provided yet (Q9): nothing is charged.
    /// </summary>
    internal sealed record TravellersIncomplete(bool DocumentsRequired) : CheckoutFailure;

    /// <summary>Too many payment attempts for this order or customer: no new attempt started (Payments:AttemptLimits).</summary>
    internal sealed record AttemptLimitReached : CheckoutFailure;

    /// <summary>
    /// The payment was authorized but the order will not book on it (its offer expired meanwhile, or the held amount is
    /// not the order's total). The unused hold is noted on the order's timeline; releasing it comes with the void step
    /// (payment-lifecycle.md), a gate for exposing checkout.
    /// </summary>
    internal sealed record AuthorizedButNotBookable(Guid PaymentId) : CheckoutFailure;
}

/// <summary>
/// The checkout's payment step (ADR 0005: revalidate → authorize → book → capture). For the customer's own order
/// awaiting payment: first finish any attempt already made with this key (its hold must never become unreachable);
/// otherwise revalidate every item with the supplier now, adopt the fresh terms (a new expiry; a price only with the
/// customer's accepted quote), and authorize the server-side total through Payments.Contracts. Once authorized, move the
/// order to Booking and book it with the supplier (<see cref="FlightBookingOrchestrator"/>): capture follows only a
/// confirmed booking. Idempotent by the payment key; an unknown payment outcome is never booked on, and a booking is sent
/// only by the request whose move to Booking was saved.
/// </summary>
internal sealed class AuthorizeCheckoutHandler(
    IOrderStore store, IFlightSelections selections, IOrderPayments payments, IOrderTravellers travellers, FlightBookingOrchestrator booking, TimeProvider timeProvider)
{
    /// <summary>
    /// How long an offer must still be valid to start a payment: the authorization and the booking both need time, and
    /// holding funds for an offer that expires meanwhile helps no one.
    /// </summary>
    internal static readonly TimeSpan MinimumOfferValidity = TimeSpan.FromMinutes(2);

    public async Task<Result<CheckoutResult, CheckoutFailure>> HandleAsync(AuthorizeCheckout command, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckoutAsync(command, cancellationToken);
        }
        catch (OrderKeptChangingException)
        {
            // The order could not be read consistently, and nothing was changed by this request after that point (the
            // booking path below answers for itself): the customer tries again with the same key.
            return Failure(new CheckoutFailure.TryAgain());
        }
    }

    private async Task<Result<CheckoutResult, CheckoutFailure>> CheckoutAsync(AuthorizeCheckout command, CancellationToken cancellationToken)
    {
        if (!Order.IsValidCustomerId(command.CustomerId) || string.IsNullOrWhiteSpace(command.IdempotencyKey) || string.IsNullOrWhiteSpace(command.PaymentMethodToken))
        {
            return Failure(new CheckoutFailure.InvalidRequest());
        }

        if (await store.FindOwnedAsync(command.OrderId, command.CustomerId, cancellationToken) is not { } order)
        {
            return Failure(new CheckoutFailure.NotFound());
        }

        // Already authorized (a replay, or a second tab): the booking is under way or done; never a second hold or booking.
        if (order.PaymentAuthorizationId is { } authorization && Guid.TryParse(authorization, out var paymentId))
        {
            return Success(Booking(order, paymentId));
        }

        if (order.Status is not OrderStatus.AwaitingPayment)
        {
            return Failure(new CheckoutFailure.NotAwaitingPayment(order.Status));
        }

        // An attempt already made with this key is finished first, whatever the supplier says now: after a timeout the
        // funds may be held, and only this lookup can find them.
        if (await payments.ResumeAsync(order.Id, command.CustomerId, command.IdempotencyKey, command.CorrelationId, cancellationToken) is { } resumed)
        {
            return await Complete(order, resumed, command, cancellationToken);
        }

        if (await Revalidate(order, Context(command), cancellationToken) is { } unavailable)
        {
            return Failure(unavailable);
        }

        // No payment before the travellers (and, when the supplier requires them, their documents) are complete (Q9).
        var incomplete = await TravellersIncompleteAsync(order, cancellationToken);
        if (!await store.TrySaveAsync(cancellationToken))
        {
            return Failure(new CheckoutFailure.TryAgain());
        }

        if (incomplete is not null)
        {
            return Failure(incomplete); // the refreshed terms are saved (e.g. documents now required); nothing is charged
        }

        var payment = await payments.AuthorizeAsync(
            new OrderPaymentRequest(order.Id, order.CustomerId, command.IdempotencyKey, order.Total, command.PaymentMethodToken, command.CorrelationId), cancellationToken);
        if (!payment.IsSuccess)
        {
            return Failure(payment.Error switch
            {
                OrderPaymentFailure.IdempotencyKeyReused => new CheckoutFailure.IdempotencyKeyReused(),
                OrderPaymentFailure.InvalidPaymentMethod => new CheckoutFailure.InvalidPaymentMethod(),
                OrderPaymentFailure.PaymentInProgress => new CheckoutFailure.PaymentInProgress(),
                OrderPaymentFailure.AttemptLimitReached => new CheckoutFailure.AttemptLimitReached(),
                _ => new CheckoutFailure.InvalidRequest(),
            });
        }

        return await Complete(order, payment.Value, command, cancellationToken);
    }

    /// <summary>Whether the order's travellers are complete for its needs; the reason not, if any.</summary>
    private async Task<CheckoutFailure?> TravellersIncompleteAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Items.Select(i => i.TravellerNeeds).FirstOrDefault(n => n is { IsKnown: true }) is not { } needs)
        {
            return new CheckoutFailure.TravellersIncomplete(DocumentsRequired: false); // an order from before traveller needs were recorded
        }

        var documentsRequired = order.Items.Any(i => i.TravellerNeeds is { DocumentsRequired: true });
        var provided = await travellers.GetReadinessAsync(order.Id, order.CustomerId, cancellationToken);
        var complete = provided.Adults == needs.Adults && provided.Children == needs.Children && provided.Infants == needs.Infants
            && provided.ContactProvided
            && (!documentsRequired || provided.DocumentsProvided == needs.Adults + needs.Children + needs.Infants);
        return complete ? null : new CheckoutFailure.TravellersIncomplete(documentsRequired);
    }

    /// <summary>Revalidates each item and adopts its fresh terms; the first reason it cannot be paid for, if any.</summary>
    private async Task<CheckoutFailure?> Revalidate(Order order, TransitionContext context, CancellationToken cancellationToken)
    {
        foreach (var item in order.Items)
        {
            var revalidated = await selections.RevalidateAsync(item.SelectedOfferId, order.CustomerId, cancellationToken);
            if (!revalidated.IsSuccess)
            {
                return revalidated.Error switch
                {
                    FlightSelectionUnavailable.NeedsPriceCheck => new CheckoutFailure.PriceChanged(item.Id),
                    FlightSelectionUnavailable.Expired or FlightSelectionUnavailable.NotFound => new CheckoutFailure.OfferExpired(item.Id),
                    FlightSelectionUnavailable.SoldOut => new CheckoutFailure.SoldOut(item.Id),
                    FlightSelectionUnavailable.SupplierCannotBook => new CheckoutFailure.SupplierCannotBook(item.Id),
                    _ => new CheckoutFailure.TryAgain(),
                };
            }

            var bookable = revalidated.Value;
            var consent = bookable is { AcceptedPriceQuoteId: { } quote, PriceAcceptedAt: { } acceptedAt } ? new PriceConsent(quote, acceptedAt) : null;
            var refreshed = order.RefreshOffer(item.Id, bookable.AgreedTotalPrice, bookable.OfferExpiresAt, consent, context, bookable.DocumentsRequired);
            if (!refreshed.IsSuccess)
            {
                return refreshed.Error is OrderTransitionError.PriceNotAccepted
                    ? new CheckoutFailure.PriceChanged(item.Id)
                    : new CheckoutFailure.NotAwaitingPayment(order.Status);
            }

            if (bookable.OfferExpiresAt - context.At < MinimumOfferValidity)
            {
                return new CheckoutFailure.OfferExpired(item.Id);
            }
        }

        return null;
    }

    /// <summary>Books on an authorized payment of exactly the order's total; any other outcome leaves the order awaiting payment.</summary>
    private async Task<Result<CheckoutResult, CheckoutFailure>> Complete(Order order, OrderPaymentResult payment, AuthorizeCheckout command, CancellationToken cancellationToken)
    {
        if (payment.Status is not OrderPaymentStatus.Authorized)
        {
            return Success(new CheckoutResult(order.Id, payment.Status switch
            {
                OrderPaymentStatus.ActionRequired => CheckoutStatus.ActionRequired,
                OrderPaymentStatus.Declined => CheckoutStatus.Declined,
                OrderPaymentStatus.Pending => CheckoutStatus.PaymentPending,
                OrderPaymentStatus.ManualReview => CheckoutStatus.PaymentManualReview,
                _ => CheckoutStatus.PaymentFailed,
            }, payment.PaymentId, payment.CustomerAction));
        }

        var context = Context(command);
        var reference = payment.PaymentId.ToString();
        if (payment.Amount != order.Total)
        {
            // Held for an earlier total (the price changed while this attempt was unresolved): never booked on.
            return await Unused(order, payment.PaymentId, "the held amount is not the order's total", context, cancellationToken);
        }

        // Travellers are checked again here: a resumed attempt (after a challenge, or from before they were required)
        // skipped the check above, and they may have changed while the payment was being authorized.
        if (await TravellersIncompleteAsync(order, cancellationToken) is not null)
        {
            return await Unused(order, payment.PaymentId, "the travellers were not complete when booking was to start", context, cancellationToken);
        }

        if (!order.StartBooking(reference, context).IsSuccess)
        {
            return await Unused(order, payment.PaymentId, "the order could not start booking (its offer expired)", context, cancellationToken);
        }

        if (await store.TrySaveAsync(cancellationToken))
        {
            // This request moved the order to Booking, so it (and only it) books: authorize → book → capture. From here the
            // request's abort token no longer applies: a customer closing the page must not cut the supplier write or the
            // save of its outcome (the supplier calls have their own timeouts).
            try
            {
                return Success(Booking(await booking.BookAsync(order, context, CancellationToken.None), payment.PaymentId));
            }
            catch (OrderKeptChangingException)
            {
                // Booking started and was saved (its authorization is stored), but its outcome could not be read back:
                // pending is true, and reconciliation settles it by lookup (never books again).
                return Success(new CheckoutResult(order.Id, CheckoutStatus.BookingPending, payment.PaymentId));
            }
        }

        // Another request changed the order first. The payment is idempotent by key, so repeating this request converges;
        // the request that moved the order to Booking books it.
        var current = await store.FindOwnedAsync(order.Id, command.CustomerId, cancellationToken);
        return current?.PaymentAuthorizationId == reference
            ? Success(Booking(current, payment.PaymentId))
            : Failure(new CheckoutFailure.TryAgain());
    }

    private async Task<Result<CheckoutResult, CheckoutFailure>> Unused(Order order, Guid paymentId, string reason, TransitionContext context, CancellationToken cancellationToken)
    {
        PaymentHolds.RequestRelease(store, order, paymentId, reason, context);
        if (await store.TrySaveAsync(cancellationToken))
        {
            return Failure(new CheckoutFailure.AuthorizedButNotBookable(paymentId));
        }

        // Lost a race: another request with this key may have started booking on this very payment. Say what is true.
        var current = await store.FindAsync(order.Id, cancellationToken);
        return current?.PaymentAuthorizationId == paymentId.ToString()
            ? Success(Booking(current, paymentId))
            : Failure(new CheckoutFailure.TryAgain());
    }

    /// <summary>Where the booking stands, for the customer: confirmed, still being settled, or not made.</summary>
    private static CheckoutResult Booking(Order order, Guid paymentId) => new(order.Id, order.Status switch
    {
        OrderStatus.Confirmed or OrderStatus.PartiallyConfirmed => CheckoutStatus.Booked,
        OrderStatus.Failed => CheckoutStatus.BookingFailed,
        _ => CheckoutStatus.BookingPending,
    }, paymentId);

    private TransitionContext Context(AuthorizeCheckout command) =>
        new(timeProvider.GetUtcNow(), CreateFlightOrderHandler.Actor(command.CustomerId), command.CorrelationId);

    private static Result<CheckoutResult, CheckoutFailure> Success(CheckoutResult result) => Result<CheckoutResult, CheckoutFailure>.Success(result);

    private static Result<CheckoutResult, CheckoutFailure> Failure(CheckoutFailure failure) => Result<CheckoutResult, CheckoutFailure>.Failure(failure);
}
