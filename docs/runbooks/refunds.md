# Cancellations and refunds

**When:** a customer asks to cancel a confirmed booking, the supplier cancels one, or a goodwill refund is agreed.
**Owner:** operations open cases (`refunds.request`); finance approves them (`refunds.approve`, the Finance role). Background: ADR 0027.

## Rules
- **Supplier first.** A cancellation is recorded only after the supplier (or consolidator) desk has cancelled the booking. Its cancellation reference is the evidence. The supplier API cannot cancel yet (Amadeus R8).
- **The server computes the refund.** For a cancellation, it is what the supplier refunds for those items (never more than the customer paid for them), less our cancellation fee (`Refunds:CancellationFee:{currency}`, none by default; it applies only once disclosed in the terms, pending legal confirmation), and never more than can still be refunded. A goodwill refund names its amount, and it is never more than can still be refunded.
- **Two people.** Every refund opened by staff is approved by a different person (ADR 0027 §5). Nobody approves their own case, compared by staff id or account.
- **Only to the card.** The refund goes back to the payment it came from, never anywhere else.
- **Never twice.** Each request carries an `Idempotency-Key` header: a repeat returns the same case, and the same key for another request is refused (`idempotency-conflict`). Payments reserves the amount on the payment (never beyond what was captured, even for refunds at the same moment) and refunds under our key `{caseId}:refund`. An outcome that is unknown is looked up by that key, never sent again.

## Steps
1. **Open the case:** `POST /api/admin/v1/orders/{orderId}/refund-cases` with a ticket reference.
   - **Cancellation:** `{ "kind": "Cancellation", "itemIds": [...], "supplierReference": "<desk reference>", "supplierRefund": "<what the supplier refunds us>" }`. The items become `Cancelled` at once (a fact), and the case shows the computed amount.
   - **Goodwill:** `{ "kind": "Goodwill", "amount": "<amount>" }`.
   - Answers: `not-captured` (nothing was charged), `item-not-cancellable` (not a confirmed item of this order), `exceeds-refundable` (other refunds or open cases hold the money: settle or reject them first; a refund is never shrunk silently).
2. **Approve or reject:** a different person uses `POST /api/admin/v1/refund-cases/{caseId}/decision` with `{ "approve": true|false, "reason": ... }`. The approvers' list is `GET /api/admin/v1/refund-cases`. A case waits at most 7 days; after that, reject it and open a new one. Only the person who opened a case may withdraw it: `POST .../{caseId}/withdrawal`.
3. **Payments refunds it:** the Worker job `payments.execute-refunds` sends the refund once. The case becomes `Refunded`, and the customer gets "Your refund has been sent".

`GET /api/admin/v1/orders/{orderId}/refund-cases` shows every case of an order and its status: `PendingApproval`, `Approved`, `Rejected`, `Refunded`, `RefundFailed`, or `NoRefund` (a cancellation with nothing to refund).

## Alerts
- **`RefundNotPossible`** (P2): the approved refund would exceed what was captured, or the payment was not captured. It was not sent, and the case becomes `RefundFailed`. Check other refunds of the payment (`payments.Refunds` by `AttemptId`). Open a corrected case if needed.
- **`RefundFailed`** (P2): the provider could not return the money. Check the provider's dashboard with `ProviderRefundId`, and contact the customer through support (Q12).
- **`RefundUnresolved`** (P1 if near the customer's legal deadline, otherwise P2): the outcome is still unknown or pending after 24 hours, the provider refused it for a reason that proves nothing (a key used for other details), or the provider holds a refund under our key that is not as expected. The record is `ManualReview` in `payments.Refunds`. Check it in the provider's dashboard by our key. **Never refund again from the dashboard without checking**: the key prevents a second refund only through our API.
- **`RefundCaseOverdue`** (P2): an approved case is not refunded within `Refunds:ExecutionTargetDays` (7). Check `payments.Refunds` for the case id; if there is no row, the Orders outbox message may have been given up on (`orders.OutboxMessages` with `FailedAt`).

The amount held back for a refund in manual review is released only when it is settled as failed; a staff path to settle it by a provider lookup comes next (Batch D). Until then, escalate to engineering.

## Do NOT
- Do not edit `orders.RefundCases`, `payments.Refunds` or an item's status in the database. That skips the approval, the audit and the one-refund guarantee.
- Do not refund from the provider's dashboard while a case is open for the same money.
