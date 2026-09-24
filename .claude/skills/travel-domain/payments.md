# Payments: domain reasoning

**Canonical design: ADR 0006 and `docs/architecture/payment-lifecycle.md`.**

## Merchant of record (open business decision)
- **We are merchant**: we charge the customer and pay suppliers (bed banks, aggregators with balance/credit). We carry chargeback, FX, and refund-timing risk.
- **Airline/supplier is merchant**: the customer's card is passed to the supplier (e.g. BSP card sales), and we may only charge a service fee. This needs card data flowing to the supplier, which changes PCI scope dramatically.
- A **mixed** model is common (hotel merchant model, flights via an aggregator balance).
- Selling flight + hotel together can create **package travel** obligations (EU PTD, UK ATOL): insolvency protection and liability for the whole package.

This is listed in `docs/requirements/open-questions.md` and blocks the final payment design.

## Stripe concepts we rely on
- **PaymentIntent** with `capture_method=manual`: authorize now, capture later (full or partial), or cancel (void).
- **Idempotency keys** on every create/capture/refund request. We use our own stable IDs.
- **Webhooks** (`payment_intent.succeeded`, `payment_intent.payment_failed`, `charge.refunded`, `charge.dispute.created`, …): at-least-once, **not ordered**, and signed. Verify, dedupe by event ID, and process async.
- **SCA / 3-D Secure**: required for many EU/UK cards. The customer may have to complete a challenge, so the flow must handle `requires_action` and abandonment.
- **Elements / Checkout**: card data goes directly to Stripe, which keeps us in PCI DSS **SAQ-A** scope.
- **Radar**: fraud scoring and rules. Travel is a high-fraud vertical (stolen cards buying last-minute flights).
- **Disputes / chargebacks**: can arrive months later. We need evidence: the timeline, T&C acceptance, e-ticket usage.

## Currency
- **Presentment currency** (what the customer pays) vs **supplier currency** (what we owe) vs **settlement currency** (what Stripe pays out). Differences create FX exposure. Record the rate and source whenever converting.
- Minor units differ per currency (JPY has 0 decimals, KWD has 3). Convert to provider minor units only inside the adapter.

## Refunds
- Refunds are **separate records** with their own lifecycle (requested → approved → submitted → succeeded/failed), idempotent, and bounded by captured minus already refunded.
- Customer refund timing is independent of the supplier refunding us. Reconciliation tracks both.
- High-value or manual refunds need **maker-checker** approval in admin.

## Reconciliation (finance)
Three-way: our payment records ↔ Stripe balance transactions/payouts ↔ supplier statements/invoices. Mismatches go to a finance queue. Without this, money leaks silently.
