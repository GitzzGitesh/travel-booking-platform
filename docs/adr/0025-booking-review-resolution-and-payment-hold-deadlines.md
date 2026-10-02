# 0025. Resolving bookings in manual review, and payment hold deadlines (Q15)

- **Status:** Accepted (2026-10-02), under the decision authority the project owner delegated on 2026-10-02. Supersedes nothing: it completes ADR 0021's manual review
- **Date:** 2026-10-02
- **Deciders:** Engineering lead (delegated authority)
- **Related:** [0021](0021-booking-orchestration-supplier-booking-and-capture.md), [0022](0022-staff-access-module-and-audit-log.md), [0024](0024-notifications-module-and-email-delivery.md), [0019](0019-amadeus-first-production-flight-supplier.md) R6–R8, Q15

## Context
ADR 0021 sends these cases to `ManualReview`, never charging and never cancelling automatically:
- a supplier booking not as agreed (`Mismatch`);
- an outcome still unknown after 24 hours;
- a refused capture or a lapsed hold.

A staff check today can only repeat the supplier lookup. Nothing ends a case, while the card hold lapses after the provider's hold lifetime (about 7 days for cards, not yet confirmed for our account). The flight port has no cancel or void operation, and Amadeus cancellation is unconfirmed (R8).

## Decision
1. **Staff end each case with one recorded outcome.** The permission is `bookings.review.resolve`. Each outcome needs a ticket reference, is audited, and is appended to the timeline:
   - **Not booked**, only when a supplier lookup shows no booking: the item becomes `Failed` and the hold is released.
   - **Cancelled at the supplier**, done at the supplier or consolidator desk, with its reference recorded as evidence. The item becomes `Cancelled` and the hold is released. Supplier fees are recorded on the case, for finance.
   - **Accept as booked**, only when the passengers and flights match what the customer agreed. The item becomes `Confirmed` and the agreed amount is captured, never more.
2. **Price rules for Accept as booked.**
   - **Lower supplier price:** charge the agreed amount. The customer consented to it, and the difference is margin.
   - **Higher supplier price:** we absorb the difference only up to `Orders:Review:AbsorbIncreaseUpTo` per currency. The default is none, so no increase is absorbed. Above it, or with no allowance, the outcome must be Cancelled (or, once ADR 0024's customer consent flow exists, the customer's consent).
   - **Two people:** absorbing any increase needs maker-checker (`bookings.review.approve-loss`).
   - **Never:** the customer is never charged more than they agreed.
3. **Different passengers or flights are never accepted.** They are cancelled at the supplier, or amended there and then checked again by lookup.
4. **A failed capture after a confirmed booking** (refused, or the hold lapsed) stays in review. The customer is told the booking is pending.
   - **Policy default:** cancel at the supplier while it can still be voided, rather than keep a booking we cannot charge for.
   - **Later:** a re-payment request to the customer (ADR 0024) is a later option, still configurable.
5. **Hold deadlines.** Payments records `AuthorizedAt` and derives `HoldExpiresAt = AuthorizedAt + Payments:Holds:Lifetime`.
   - **Default:** 7 days, a placeholder until the provider confirms it for our account.
   - **Alert:** a Worker job raises `PaymentHoldExpiring` once per attempt, `Payments:Holds:WarningBefore` (default 48 hours) before expiry, for any held payment not captured or released.
   - **Shown:** the deadline appears on the staff payment page.
6. **Reconciliation keeps running.** A lookup can still settle a case before a person does, and every outcome rechecks the current state (concurrency by rowversion).

## As built (2026-10-02)
- **Cancelled at the supplier:** only for a booking seen not as agreed (an item whose outcome is unknown leaves review only through a supplier lookup). The item becomes `Failed`, with the desk reference as the timeline's provider reference and the reason "cancelled at the supplier". There is no separate `Cancelled` state: nothing is booked any more, which is what `Failed` means for the payment. `Cancelled` arrives with refunds (ADR 0027), for confirmed items cancelled later.
- **Not booked:** already covered by the supplier check (`review-checks`), which fails an item the supplier has no booking for after its window. A person's statement never does.
- **Accept as booked:** only for an item whose supplier booking was seen not as agreed (`provider:locator` on the timeline). It needs the person's two confirmations (same travellers and flights, price not above the agreed one) and charges the agreed price.
- **Absorbing a higher price:** not built. The allowance default is none, so such a booking is cancelled at the supplier. The maker-checker path (`bookings.review.approve-loss`) comes if an allowance is ever configured.
- **Failed capture after a confirmed booking:** stays with the payment review and the hold watch (point 5). Cancelling a confirmed item comes with refunds (ADR 0027).

## Consequences
**Positive**
- Every review case can end, money follows the outcome, and the hold is never forgotten.
- No supplier capability is assumed: desk cancellation is evidence recorded by a person.

**Negative / trade-offs**
- Manual work per case until the supplier API supports cancelling (R8).
- Cancelling a booking whose capture failed is customer-unfriendly, but money-safe. It is revisited with the re-payment flow.

**Pending**
- The real hold lifetime (provider).
- R8 (supplier cancel or void, and the void window).
- Customer consent for higher prices (needs ADR 0024 delivery).
