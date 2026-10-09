# Payment hold not released

**Alert:** (to be wired with observability) a payment attempt in `ManualReview`, `VoidUnknown` for more than an hour, or with a release requested and still `Authorized` after 15 minutes; or an Orders outbox message with `FailedAt` set.
**Severity:** P2 (the customer's funds are held with nothing booked)  **Owner:** payments operations
**Customer impact:** an authorization hold stays on the customer's card until it is released or lapses at the issuer. Nothing is captured.

## Diagnose
All queries are read-only.
1. The attempt: `payments.PaymentAttempts` by `Id` (the payment id on the order timeline) or `OrderId`: its `Status`, `ReleaseRequestedAt`, `ProviderId` and `ProviderPaymentId`.
2. Its history: `payments.PaymentAttemptEvents` by `PaymentAttemptId`, in `Id` order. Each row has the actor, the correlation id, and the provider reference.
3. The order: `orders.OrderTimeline` shows "Payment not used: … The hold must be released" with the payment id as the provider reference.
4. The release request: `orders.OutboxMessages` where the payload contains the payment id. `ProcessedAt` set means Payments recorded the request; `FailedAt` set means dispatch gave up (`LastError` has the exception type).

## Resolve
### Case A: `VoidUnknown` or `Voiding` for a long time
The reconciliation job looks the payment up every run and repeats the void with the same key while the payment is still authorized. Check the Worker is running and `payments.JobLeases` shows `payments.reconcile-attempts` renewing. If the provider is down, wait for it to recover.

### Case B: `ManualReview`
The provider reported something unexpected: a different amount, a captured payment, a void it refused, or a payment it no longer finds. Compare with the provider's dashboard using `ProviderPaymentId`, then resolve it with `POST /api/admin/v1/payments/{attemptId}/review-resolutions`. This needs the `payments.review.resolve` permission, and the reason is a ticket reference. `GET /api/admin/v1/payments/{attemptId}` shows the attempt and its history. Each resolution is audited in the same save.
- It looks the payment up with the provider and moves the attempt only to what the provider holds:
  - Authorized (this attempt's amount: then released by reconciliation if Orders asked, or used as usual);
  - Declined, Canceled, Expired, Voided, or Failed (not found after the consistency window).
- A capture, another amount, a failed lookup or "not found" too early leave it in review. The check is recorded, and you escalate.
- Repeating the resolution changes nothing.
- A payment the provider once had but no longer finds stays in review: check the Stripe account and key in use before anything else.
- **Alert `PaymentHoldNotReleasable`:** a hold is to be released but has no provider payment id to void. Void it in the provider's dashboard, then record it with finance.
- **Alert `PaymentReleaseUnresolved`:** a release (void) stayed unresolved for 24 hours after it began, because the provider kept refusing the lookup. The attempt is in `ManualReview`. Once the provider answers lookups again, resolve the review with what it shows. If the payment is still held, resolve it to `Authorized`, and the Worker releases it again with a new void. Never void it by hand (see "Do NOT").

### Case C: outbox message given up (`FailedAt` set)
The release request never reached Payments. Find the cause from `LastError` and the Worker logs (search for the message id). Once it is fixed, a database administrator can clear `FailedAt` and set `NextAttemptAt` to now on that one row. Redelivery is safe, because the Payments inbox ignores duplicates.

**After a deploy or a rollback:** outbox messages are stored under each event's declared name (`orders.OrderPaymentReleaseRequested`, …). A Worker older than the declared names cannot dispatch those messages; it gives them up after about 13 minutes with "Unknown integration event type". So deploy the Worker together with, or before, the Api, and apply the Payments migrations first. Never roll back past the declared names without draining the outbox. If messages were given up this way, re-queue them as above once the current Worker is live. A capture request given up this way means a confirmed booking was never charged.

## Do NOT
- Do not authorize again, capture, or void the payment by hand at the provider. The Worker's void uses our key (`{attempt}:void`), and a manual void outside it leaves our record wrong.
- Do not update `PaymentAttempts` or delete history rows by hand. History is append-only.
- Do not delete outbox or inbox rows.

## Escalate
Payments engineering, with the attempt id, the order id and the correlation ids from the history.
