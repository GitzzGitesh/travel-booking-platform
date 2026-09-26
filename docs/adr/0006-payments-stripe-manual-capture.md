# 0006. Payments: Stripe, PCI SAQ-A scope, manual capture, webhooks

- **Status:** Proposed. Q1 is **answered for flights** (merchant of record, 2026-09-25), so the SAQ-A / manual-capture design applies to flights. **Not yet accepted:** accepting it also fixes the payment provider (Stripe), whose market availability, fees and settlement currencies depend on Q2 (markets) and Q5 (charge currencies); hotel merchant of record (Q1) is also open. It needs an explicit decision before any payment work. **Decision 2026-09-26:** build only a provider-neutral `IPaymentProvider` port and a deterministic mock for now (Phase 3 chunk 2). Everything Stripe-specific in this ADR (the provider, Elements/Checkout, webhooks, Radar and keys) stays pending.
- **Date:** 2026-09-24
- **Related:** [0005](0005-order-aggregate-and-booking-orchestration.md), [0007](0007-async-processing-worker-and-outbox.md), `docs/architecture/payment-lifecycle.md`, `docs/requirements/open-questions.md`

## Context

We need card payments with SCA support, minimal PCI scope, and a design that tolerates payment/booking divergence, duplicate requests, and duplicate or out-of-order webhooks. Stripe is the expected first provider; a mock provider is needed for development and tests. Whether we are merchant of record for each product is not yet decided.

## Decision

- **`IPaymentProvider` port** in the Payments module; `Integrations.Payments.Stripe` and `Integrations.Payments.Mock` implement it.
- **Stripe PaymentIntents with `capture_method=manual`**: authorize at checkout, capture after supplier confirmation, cancel (void) on booking failure. Partial capture for partially confirmed orders.
- **Card data only via Stripe Elements/Checkout**, which keeps us in **PCI DSS SAQ-A** scope. We store PaymentIntent/charge IDs, brand, last4, and expiry only.
- **Idempotency keys** on every Stripe write, derived from our IDs.
- **Webhooks**: signature verified in the Api, persisted to an inbox table unique on the Stripe event ID, processed asynchronously by the Worker, and tolerant of duplicates and out-of-order delivery. The Worker can also retrieve PaymentIntent state directly for reconciliation.
- **Refunds** are separate records with their own lifecycle and idempotency. The total refunded never exceeds the captured amount (enforced transactionally). Maker-checker applies above a threshold (Q11).
- **Fraud**: Stripe Radar enabled. Custom rules are defined later (Q10).
- Stripe secret keys and webhook secrets live in Key Vault. Only the publishable key reaches the frontend.

## Consequences

**Positive:** minimal PCI burden; voids instead of refunds for booking failures; robust webhook handling.
**Negative:** authorization hold expiry (about 7 days for most card payments) constrains delayed-confirmation flows. If the airline is merchant of record (card pass-through to BSP), SAQ-A no longer holds and this ADR must be revisited.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Automatic capture | Booking failures become refunds (fees, delay, disputes) |
| Server-side card collection | Moves us to SAQ-D. Unacceptable cost and risk |
| Webhook-only state (no direct retrieval) | Webhooks can be delayed or lost; reconciliation needs pull as well |
