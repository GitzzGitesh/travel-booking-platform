# 0027. Cancellations and refunds: staff-executed, policy-driven, maker-checker (Q11)

- **Status:** Accepted (2026-10-02), under the decision authority the project owner delegated on 2026-10-02. Consumer cancellation rights and refund timelines per market are **pending legal confirmation** (Q2)
- **Date:** 2026-10-02
- **Deciders:** Engineering lead (delegated authority)
- **Related:** [0006](0006-payments-stripe-manual-capture.md), [0021](0021-booking-orchestration-supplier-booking-and-capture.md), [0022](0022-staff-access-module-and-audit-log.md), [0024](0024-notifications-module-and-email-delivery.md), [0025](0025-booking-review-resolution-and-payment-hold-deadlines.md), [0019](0019-amadeus-first-production-flight-supplier.md) R8, Q11

## Context
We are merchant of record for flights (Q1), so refunds are our money movement. Payments already supports refunds:
- each refund is a record with our key, and a status of Pending, Succeeded or Failed;
- the total refunded never exceeds the captured amount;
- a refund is looked up by our key.

There is no refund workflow. Supplier cancellation, its fees and its fare rules are unconfirmed (R8). Consumer rights depend on markets (Q2), which are open.

## Decision
1. **A refund case per cancellation or refund**, owned by Orders, which knows the bookings. Payments executes the refund. A case is started by:
   - **the customer:** a cancellation request on their own order;
   - **staff:** `refunds.request`, with a ticket reference, including goodwill refunds.
2. **The supplier comes first.** A refund for a cancellation is executed only after the supplier cancellation is confirmed. For now that means desk evidence recorded by staff (ADR 0025); after R8, a lookup.
   - **Uncertain items:** never refunded while `PendingConfirmation` or `ManualReview`. Resolve them first.
   - **Never captured:** items that were never captured, including failed items in a partial booking, need no refund. Before capture, a hold is voided, never refunded.
3. **The amount is computed by the server and never exceeds the captured amount minus what is already refunded.**
   - **Formula:** refundable = what the supplier refunds (recorded, per item) − our cancellation fee.
   - **Fee:** `Refunds:CancellationFee` per currency. The default is **none**: a fee can only apply once it is disclosed in the terms at booking (pending legal confirmation).
   - **Fee kept back:** our booking service fee, if one is ever charged, is not refunded unless the policy says so (`Refunds:RefundServiceFee`, default true while no fee exists).
4. **Goodwill.** Staff may refund beyond the supplier's refund, up to the captured amount, only with maker-checker, and with the reason recorded.
5. **Approvals (maker-checker).** The requester and the approver are different people. The approver needs `refunds.approve`, held by a new **Finance** role and by Administrators. Approval is required for:
   - every goodwill or staff-started refund;
   - any refund on an order that was in manual review;
   - any refund at or above `Refunds:ApprovalThreshold:{currency}`.

   With **no threshold configured** (the default), **every refund needs approval.** That is the safest start, relaxed per currency by configuration.
6. **Execution.** One refund per case, with the key `{refundId}:refund` (ADR 0006).
   - **Unknown outcome:** looked up by our key, never sent again.
   - **Still unknown:** if unsettled after 24 hours, the case goes to review with the `RefundUnresolved` alert.
   - **Failed:** goes to review, never retried blindly.
   - **Destination:** refunds go to the original payment method only.
7. **Customer messages (ADR 0024):** cancellation received, refund approved and issued, refund completed or failed.
8. **Timelines.** `Refunds:ExecutionTargetDays` (default 7) raises an operations alert for cases still open past it. The legally required timelines per market are pending legal confirmation.

## Consequences
**Positive**
- No refund without a confirmed supplier outcome.
- Two people for anything discretionary.
- Every amount comes from the server and is bounded.

**Negative**
- Manual work until the supplier cancellation API is confirmed (R8).
- Two-person approval for every refund until thresholds are set.

**Pending**
- R8 and fare rules.
- Consumer rights and timelines per market.
- Real refunds need the payment provider accepted (ADR 0006).

**Deferred**
- Instant self-service cancellation with computed fees.
- Chargebacks and disputes.
- Per-passenger cancellation.
