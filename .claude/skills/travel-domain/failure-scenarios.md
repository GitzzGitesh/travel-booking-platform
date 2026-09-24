# Failure scenarios: how to reason about them

**The canonical catalog with expected behaviour and required test level is `docs/quality/failure-scenarios.md`.** Add new scenarios there, not here.

## A checklist for any supplier or payment call

For every outbound write (book, ticket, cancel, authorize, capture, refund), answer:

1. **Is it idempotent at the provider?** With which key? If not, it must never be retried automatically.
2. **What does a timeout mean?** Almost always "unknown". Which query resolves it (retrieve by our reference)?
3. **What does a 5xx mean?** Also possibly unknown for writes. Don't assume failure.
4. **What does a duplicate request do?** Our side: the unique idempotency constraint returns the original result. Provider side: the provider idempotency key or client reference.
5. **What if our process crashes right after the call succeeds?** The outbox/timeline must let the Worker recover state on restart.
6. **What if two requests race** (double-click, two tabs, webhook + poll at once)? Optimistic concurrency + idempotency means exactly one effect.
7. **What is the compensation** if a later step fails, and is the compensation itself idempotent and recorded?
8. **What does the customer see**, and what does operations see (timeline, queue, alert)?

## Common patterns
- **Payment authorized, booking failed** → void the authorization. The customer sees a clear failure with no charge.
- **Payment authorized, booking unknown** → `PendingConfirmation`, reconcile. **Do not void yet**; voiding before we know risks an unpaid live booking. Void only once the booking is confirmed absent.
- **Booking confirmed, capture failed** → retry capture (idempotent at Stripe with our key) within the authorization validity. On persistent failure, escalate to ops; don't cancel the booking automatically without a policy decision.
- **Duplicate webhook** → inbox table unique on provider event ID. The second delivery is a no-op.
- **Out-of-order webhook** → state machine ignores transitions that are older or not allowed from the current state.
- **Duplicate refund request** → idempotency key + refund total ≤ captured amount, enforced transactionally.
- **Provider outage** → circuit breaker opens; search degrades (other providers or a clear message); bookings are not attempted; alerts fire.
- **Offer expired / price changed** → a business result (422 with a type), not an error. The UI re-prices and asks the customer.
