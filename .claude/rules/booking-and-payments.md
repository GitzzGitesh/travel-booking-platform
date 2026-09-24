# Booking & payment rules

Applies to all work touching search, offers, orders, bookings, payments, refunds, pricing, webhooks, or reconciliation. Background: ADR 0005, 0006, 0010, `docs/architecture/booking-lifecycle.md`, `docs/architecture/payment-lifecycle.md`, `docs/quality/failure-scenarios.md`.

## Pricing
- **Never trust client-side prices.** The client sends an offer ID; the server loads the persisted offer snapshot and **revalidates price and availability with the supplier** before payment authorization.
- If the revalidated price differs, return an explicit price-change result that the customer must accept. Never silently charge a different amount.
- Offers carry an expiry. Expired offers cannot be booked; they must be re-searched or re-priced.
- Markups, promotions, taxes, and fees are computed server-side and stored on the order as a price-breakdown snapshot.

## Idempotency
- Every command that creates or changes a booking, payment, capture, void, refund, or cancellation takes an **idempotency key**. It is enforced by a **database unique constraint**, not just an in-memory check.
- A replayed key returns the original result. The same key with a different payload is rejected (409).
- Use our own stable reference (e.g. OrderItem ID) as the supplier booking reference/idempotency token wherever the supplier supports it, and as the Stripe idempotency key.

## Retries and unknown outcomes
- **Never blindly retry** supplier booking, ticketing, cancellation, or payment capture/refund calls. Retries are only allowed for idempotent reads (search, price check, retrieve booking) and for writes where the provider guarantees idempotency with our key.
- A timeout or ambiguous error on a write means the **outcome is unknown**. Move to `PendingConfirmation` (or the payment equivalent), record it on the timeline, and let reconciliation **query the provider by our reference**. Do not resubmit.
- Compensation (void authorization, cancel booking, refund) is explicit, recorded on the timeline, and itself idempotent.

## Flow
- Standard flow: revalidate → authorize payment (manual capture) → book with supplier → capture on confirmed booking → issue documents. Booking failure → void the authorization.
- Each order item is tracked independently. A multi-item order can end partially confirmed, and that must be representable and handled.

## State and history
- Order, booking, payment, and refund status changes go **only** through explicit state machine methods on the aggregate. No ad-hoc status assignments. Illegal transitions return errors and are tested.
- Every transition appends an entry to the **booking timeline** (append-only: actor, timestamp, from→to, reason, correlation ID, provider reference). Timeline rows are never updated or deleted.
- Aggregates use optimistic concurrency (`rowversion`). Concurrency conflicts are retried at the handler level only when the command is idempotent.

## Webhooks
- Verify the signature first, persist the raw event with a **unique provider event ID** (dedupe), acknowledge fast, and process async in the Worker.
- Handlers tolerate duplicates and out-of-order delivery. Check the current state and ignore stale transitions.

## Money, currency, time
- Money = `decimal` amount + ISO-4217 currency code as one value object. Never `double`/`float`. Never add amounts in different currencies without an explicit FX conversion that records its rate and source.
- Rounding and minor-unit handling follow ADR 0010. Convert to provider minor units only inside the adapter.
- Store instants in UTC (`datetimeoffset`). Keep flight departure/arrival as **local time + airport/IANA zone**. Hotel check-in/out are local dates. Cancellation deadlines keep the supplier's original time zone.
