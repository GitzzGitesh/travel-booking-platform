# Booking stuck in PendingConfirmation or ManualReview

**Alert:** `BookingUnresolved` (a booking still unknown 24 hours after it started), `BookingMismatch` (a supplier booking not as agreed), `BookingFoundAfterFailure` (a booking found after it was concluded absent); or the Worker job `orders.reconcile-bookings` not completing.
**Severity:** P2 (the customer's funds are held and they do not know whether they are booked); P1 for `BookingFoundAfterFailure` (booked, but the hold may already be released).  **Owner:** booking operations
**Customer impact:** the order shows as pending. Nothing is charged, and the hold stays on the card until the booking is settled.

Background: ADR 0005, ADR 0021, `docs/architecture/booking-lifecycle.md`.

## Diagnose
All queries are read-only.
1. The order: `orders.FlightOrderItems` by `OrderId`, giving `Status`, `BookingStartedAt`, `ProviderId` and `SupplierLocator`. The item id is our client reference at the supplier.
2. Its history: `orders.OrderTimeline` by `OrderId`, in `Id` order, giving each attempt and lookup with its actor and correlation id. Search the Api and Worker logs by that correlation id and by the item id (never by names).
3. The job: `orders.JobLeases` for `orders.reconcile-bookings`, and Worker log lines "Reconciling the bookings of order …".
4. The payment: `payments.PaymentAttempts` by `OrderId`. It should be Authorized, with no capture or release requested while the booking is unsettled.

## Resolve
- **PendingConfirmation, job running:** wait. The job looks the booking up by our reference, first after 30 seconds and then backing off up to every 30 minutes. Found means Confirmed and then the charge. Not found after 15 minutes since the booking started means Failed and then the release. If the supplier's lookup keeps failing (an outage), the item stays pending. That is safe.
- **ManualReview (`BookingUnresolved` or `BookingMismatch`):** check the booking in the supplier's own tool, using the client reference (the item id) or the locator.
  - If the supplier holds a booking **as agreed** (price, passengers, flights), it must be confirmed.
  - If it holds **none**, it must be failed, and the hold released.
  - If it holds a booking **not as agreed**, escalate to the supplier desk before anything else.

  **Check it with the supplier:** `POST /api/admin/v1/orders/{orderId}/items/{itemId}/review-checks` with a ticket reference as the reason. This needs the `bookings.review.resolve` permission. It looks the booking up by our reference and settles it only on what the supplier holds:
  - found as agreed: Confirmed, then charged;
  - absent after the consistency window: Failed, then released;
  - anything else: it stays in review, with the check on the timeline.

  The payment's settlement and the audit entry are saved with it. A booking that exists but not as agreed stays in review: escalate it to the supplier desk, then **record the outcome** (ADR 0025): `POST /api/admin/v1/orders/{orderId}/items/{itemId}/review-outcomes` (admin-web: **Record an outcome** on the order page), with a ticket reference:
  - `CancelledAtSupplier` with the desk's cancellation reference (`supplierReference`), only for a booking seen not as agreed: the item is Failed, nothing is charged for it, and the hold follows the order's settlement. Record any supplier fee on the ticket for finance.
  - `AcceptAsBooked`, only after comparing the supplier's booking with the order: `sameTravellersAndFlights` and `priceNotAboveAgreed` must both be true. The item is confirmed under the seen supplier reference, and the agreed price is charged, never more. A higher supplier price is never absorbed today (`Orders:Review:AbsorbIncreaseUpTo` is none): cancel it at the supplier instead. Both are refused (`no-supplier-booking-seen`) when no booking was seen under our reference: such an item (outcome unknown) leaves review only through the supplier check, never by a statement that releases the hold.
  - The customer gets the matching notice (ADR 0024).

  **Deadline:** the payment hold lapses about 7 days after authorization (`payments.PaymentAttempts.CreatedAt`). A confirmed booking must be charged before then.
- **`BookingFoundAfterFailure`:** the customer is booked, but the hold may already be released. Check `payments.PaymentAttempts`, and escalate to payments operations at once: re-authorize, or cancel the booking with the supplier within its free period.

## Do NOT
- Do not book again at the supplier for a pending item. The first booking may exist; a second one would double-book the customer.
- Do not capture or void by hand at the payment provider. Settlement follows the booking's state through the outbox.
- Do not edit `FlightOrderItems` or delete timeline rows.

## Escalate
Booking engineering and the supplier desk, with the order id, the item id (our reference), the locator and the correlation ids from the timeline.
