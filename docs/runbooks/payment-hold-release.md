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
The provider reported something unexpected: a different amount, a captured payment, a void it refused, or a payment it no longer finds. Compare with the provider's dashboard using `ProviderPaymentId`. Resolving it needs the operator path, which does not exist yet (`docs/progress.md`). Until then, escalate.

### Case C: outbox message given up (`FailedAt` set)
The release request never reached Payments. Find the cause from `LastError` and the Worker logs (search for the message id). Once it is fixed, a database administrator can clear `FailedAt` and set `NextAttemptAt` to now on that one row. Redelivery is safe, because the Payments inbox ignores duplicates.

## Do NOT
- Do not authorize again, capture, or void the payment by hand at the provider. The Worker's void uses our key (`{attempt}:void`), and a manual void outside it leaves our record wrong.
- Do not update `PaymentAttempts` or delete history rows by hand. History is append-only.
- Do not delete outbox or inbox rows.

## Escalate
Payments engineering, with the attempt id, the order id and the correlation ids from the history.
