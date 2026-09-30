# 0021. Booking orchestration: synchronous supplier booking, reconciliation and capture

- **Status:** Accepted (2026-09-30) by the project owner, as written
- **Date:** 2026-09-29
- **Deciders:** Project owner
- **Related:** [0005](0005-order-aggregate-and-booking-orchestration.md), [0007](0007-async-processing-worker-and-outbox.md), [0015](0015-synchronous-cross-module-commands-for-checkout.md) (extends its list of commands), [0019](0019-amadeus-first-production-flight-supplier.md), [0020](0020-traveller-personal-data-store-and-retention.md), `docs/architecture/booking-lifecycle.md`, `docs/architecture/payment-lifecycle.md`

## Context

ADR 0005 fixes the checkout order: authorize → book → capture, with unknown supplier outcomes going to `PendingConfirmation`. The pieces existed separately:
- `AuthorizeCheckoutHandler` moved the order to `Booking`;
- `FlightSupplierBooking` (Flights) made one classified supplier write;
- Payments could authorize and void.

Nothing connected them, and there was no checkout endpoint. ADR 0015 allows synchronous cross-module commands only for revalidation and payment authorization, so booking the supplier from the checkout needs this decision.

## Decision

1. **The supplier booking is a synchronous checkout command** through `Modules.Flights.Contracts.IFlightBookings`. This extends ADR 0015's list, and it meets all five of ADR 0015's conditions:
   - The customer waits for the answer, which is booked, not booked, or pending.
   - It is idempotent by our reference, the order item id: the supplier's client reference wherever supported. It is called only by the request whose move to `Booking` was saved.
   - It is supplier-neutral: our types only.
   - Unknown outcomes are explicit, and are settled by a lookup, never by resubmitting the booking.
   - Only Orders may call it (architecture test).
2. **Travellers go to the supplier only for the booking.** Orders reads them through `IOrderTravellers.GetForBookingAsync`, and every document read is audited (actor `system:flight-booking`). They are passed in memory to Flights and the adapter, and are never stored outside Customers. If they cannot be read, nothing is sent: the item fails and the hold is released.
3. **Outcomes:**

   | Supplier result | Item state | Payment |
   |---|---|---|
   | Booked | `Confirmed`, with the locator (PNR) and ticketing state | Charged |
   | NotBooked (a definitive refusal) | `Failed` | Released |
   | Unknown | `PendingConfirmation` | Neither |
   | Mismatch | `ManualReview` | Never charged |

   Checkout answers 200 (booked or not booked) or 202 (pending: poll the order).
4. **Reconciliation** is the Worker job `orders.reconcile-bookings`, every 30 seconds, configured by `Orders:BookingReconciliation`. It looks up items that are `PendingConfirmation`, and items left in `Booking` longer than `LookupAfter` (5 minutes, which covers a crash or a lost save), by our reference:
   - found → `Confirmed`;
   - not found → `Failed`, but only after `NotFoundConclusiveAfter` (15 minutes since booking started);
   - still unknown after `ManualReviewAfter` (24 hours) → `ManualReview`, with the `BookingUnresolved` alert.

   It never books. Lookups that settle nothing back off: 30 seconds, then doubling up to 30 minutes. The most overdue come first, so an outage never starves newer bookings. Each run carries a correlation id.

   The windows count from the move to `Booking`. So the request refuses to send a booking once `LookupAfter / 2` has passed since then (NotBooked, nothing sent, hold released): a send is never still in flight when reconciliation concludes. From the saved move to `Booking` onwards, the booking and its save ignore the HTTP request's abort, and the supplier calls keep their own timeouts. If the outcome still cannot be saved, the customer is told what is stored (pending), never an unsaved outcome.
5. **Payment settlement happens once per order,** when no item is still booking, pending or in review. The charge covers the confirmed items (a partial capture releases the rest); with nothing confirmed, the hold is released. It is published through the Orders outbox in the **same save** as the final transition, so a crash never loses it (F-25).
   - Payments records `OrderPaymentCaptureRequested` once (inbox). Its reconciliation job then captures with one key, `{attempt}:capture`: saved as `Capturing` before the call; an unknown outcome is looked up and repeated with the same key only while the hold is still there.
   - A refused capture, or a lapsed or voided hold, goes to `ManualReview` with the `PaymentCaptureFailed` alert (F-23, F-24). The booking is never cancelled automatically.
   - A hold with a capture requested is never released, not even on a provider notification. A notification about an attempt being charged resumes the capture instead.
   - The capture key starts a new generation after each manual review, as the void key does. A capture whose outcome stays unknown for 24 hours goes to a person before the hold (about 7 days) can lapse.
6. **Supplier readiness is unchanged.** An adapter that cannot book and look up bookings is refused before payment (`SupplierCannotBook`). Amadeus stays so until its booking ADR (ADR 0019). The mock books deterministically, with instant ticketing, and has its timeout scenarios.

## Consequences

- **Positive:** the first bookable path is complete, and no step ever holds or charges money without its evidence. Every unknown is looked up by our own reference.
- **Negative:**
  - Checkout is as slow as the supplier's booking call. Beyond a few seconds, the 202 path covers it.
  - The reconciliation windows are global: the widest window any composed supplier needs. A supplier's ADR states its own.
  - Ticketing is recorded but not driven. Fulfilment (`Confirmed → Fulfilled`) and cancellation come later.
  - The mock keeps its bookings in memory per process, so a separately started Worker cannot find the Api's mock bookings. Development only.

## Alternatives considered

- **Book in the Worker only (an outbox event to Flights):** the customer would always poll, and Flights would need an outbox, an inbox, and traveller data in an event. There is no safety gain, because the same unknown-outcome handling applies either way.
- **Capture synchronously in the checkout request:** a capture timeout would then need a second recovery path in Orders. Through the outbox and the existing Payments reconciliation, one path handles it.
