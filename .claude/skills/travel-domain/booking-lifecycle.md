# Booking lifecycle: domain reasoning

**Canonical state machines: `docs/architecture/booking-lifecycle.md`.** This file explains *why* they look the way they do.

## Why an Order with items
Customers think in trips; the finance and ops teams think in payments and refunds. An **Order** groups items (a flight, a hotel, later ancillaries and insurance) under one checkout and one payment. Each **item** has its own supplier, lifecycle, and possible failure. Partial success (flight confirmed, hotel failed) is normal and must be a first-class state, not an exception.

## Why authorize → book → capture
- Charging first and then failing to book means a refund: fees, delays of days for the customer, and support tickets.
- Authorizing first, then booking, then capturing means a booking failure only needs a **void** (no money moved, just a released hold).
- Constraint: authorization holds expire (card-network dependent, typically about 7 days for Stripe online card payments). Fine for instant confirmation; flows with delayed confirmation ("on request" hotels, deferred ticketing) need a design decision (capture before confirmation with a refund path, or re-authorization).

## Why "unknown" is a state
A timeout on a supplier write does not mean failure. The booking may exist. Options:
- Treat it as failed: this risks a real booking we never pay the supplier for, or a customer rebooking and being double-booked.
- Retry the write: this risks a **duplicate booking**.
- **Mark it `PendingConfirmation` and reconcile** by querying the supplier with our reference: safe. This is our rule.

Reconciliation runs in the Worker with backoff, and escalates to an operations queue after a bounded time. The customer sees "confirming your booking", not an error.

## Why a timeline
Operations and support need to answer "what happened to this booking?" without reading logs: every state change, supplier call outcome, payment event, customer notification, and manual admin action, in order, with references. It is also evidence in chargebacks and supplier disputes.

## Cancellation and refunds (domain view)
- Eligibility and amount come from **fare rules / cancellation policy snapshot**, and must be re-quoted from the supplier where possible (penalties can change).
- Flight: void (within the void window) vs cancel + refund (fare rules) vs no-show. Taxes are often refundable when fares are not.
- Hotel: free before the deadline (in the supplier's zone), penalty afterwards.
- Refund to the customer is a separate lifecycle from supplier cancellation. The supplier refund to us may arrive later, or never (reconciliation).
- Customer refunds must be idempotent and must never exceed the captured amount minus prior refunds.

## Involuntary changes
Schedule changes, cancellations by the airline, and hotel walks arrive asynchronously, sometimes months later. The lifecycle needs states or flags for "supplier-changed, awaiting customer action" and an ops queue. They're not needed in the first slice, but the model must not preclude them.
