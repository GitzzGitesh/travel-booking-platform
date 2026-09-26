# 0005. Order aggregate and booking orchestration

- **Status:** Accepted (2026-09-25), **for flights**. Its gate (Q1, merchant of record) was answered for flights: we are merchant of record. If hotels are sold on an agency model (Q1 is open for hotels), a superseding ADR must say how payment attaches to hotel items.
- **Date:** 2026-09-24
- **Related:** [0004](0004-supplier-provider-abstraction.md), [0006](0006-payments-stripe-manual-capture.md), [0007](0007-async-processing-worker-and-outbox.md), `docs/architecture/booking-lifecycle.md`, `docs/quality/failure-scenarios.md`

## Context

A checkout may eventually contain several products (flight + hotel + ancillaries) paid for once. Supplier calls are slow, can time out after succeeding, and are not transactional with our database or with payments. The critical failure is money and booking diverging: charged without a booking, or booked without payment. Blind retries can double-book.

## Decision

1. **Order aggregate**: an `Order` is the customer's checkout/trip, with `OrderItems` (`FlightBooking`, `HotelBooking`, later others). Payments attach to the Order. Order status is derived from its items and supports **partial confirmation**.
2. **Server-side pricing**: items are created from **persisted offer snapshots**. Price and availability are **revalidated** with the supplier before payment authorization. Client-sent prices are ignored.
3. **Authorize → book → capture**: authorize the card (manual capture), then book with the supplier, capture confirmed items, and void on definitive failure.
4. **Unknown is a state**: supplier write timeouts or ambiguous errors put the item in `PendingConfirmation`. The Worker **reconciles by retrieving with our client reference**. The book call is **never resubmitted**. Unresolved items escalate to `ManualReview`.
5. **Idempotency**: every command takes an idempotency key, enforced by a DB unique constraint. Our `OrderItemId` is the supplier client reference and the basis of Stripe idempotency keys.
6. **Explicit state machines** on aggregates, with optimistic concurrency (`rowversion`).
7. **Append-only timeline** per Order recording every transition, supplier call outcome, payment event, notification, and admin action.
8. Side effects (fulfilment, emails, reconciliation triggers) go through the **outbox** (ADR 0007).

## Consequences

**Positive:** most "paid but not booked" cases become voids instead of refunds; duplicates are harmless; support has a full history; multi-product checkout is possible without remodelling.
**Negative:** more states to implement and test; requires the Worker and reconciliation from the first slice; authorization expiry limits delayed-confirmation flows (on-request hotels, deferred ticketing need a separate decision).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Capture payment first, refund on failure | Refund fees and delays, customer distress, more support load |
| Book first, then take payment | Risk of unpaid live bookings; the supplier may require payment at booking |
| Separate top-level FlightBooking/HotelBooking without an Order | Multi-product checkout and payment-level refunds become a painful migration later |
| Distributed transaction / saga framework | Unnecessary. An explicit state machine + outbox + Worker is simpler and sufficient |
