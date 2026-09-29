# Personal data: document keys, purge, legal hold and payment-attempt alerts

**Alert:** (to be wired with observability) the Worker job `customers.purge-personal-data` has not completed for 24 hours; travellers' documents failing with `documents-unavailable` (503); or `PaymentAttemptLimitRepeated` (Error) in the Api logs.
**Severity:** P2 for a stopped purge (retention promises at risk) or unavailable documents (customers cannot complete checkout); P3 for `PaymentAttemptLimitRepeated`.  **Owner:** platform operations, with privacy/legal for legal holds and fraud operations for the review list
**Customer impact:** a stopped purge has none that is visible. Unavailable documents block checkout for bookings whose supplier requires documents. A customer at the attempt limit sees a generic "payment could not be completed" message, and their order stays AwaitingPayment until it expires.

Background: ADR 0020, Q9 and Q10 in `docs/requirements/open-questions.md`, `docs/architecture/security.md`.

## Configuration: the document key
- `Customers:DocumentEncryption:ActiveKeyId`: the id of the key used for new documents, e.g. `kek-2026-09`.
- `Customers:DocumentEncryption:Keys:{id}`: base64 of 32 random bytes, one entry per key id still in use.
- Locally: `dotnet user-secrets set "Customers:DocumentEncryption:Keys:kek-local" "<base64>" --project src/backend/Hosts/Api` (the same for the Worker), plus the `ActiveKeyId`. In Azure: a Key Vault secret per key, read through managed identity. **Never** put a key in `appsettings*.json`, Git, tickets or chat.
- Without a usable key, no document is stored. Customers get 503 `documents-unavailable`, and nothing else breaks.
- **Rotation (monthly):** add a new key under a new id (e.g. `kek-2026-10`), then switch `ActiveKeyId`. Keep an old id configured while live documents use it: `customers.TravelDocuments` rows with that `KeyId` and `ShreddedAt` null. Those are all shredded within 30 days after their last flight.
- **Retiring a key:** row shredding does not reach database backups or exports. A backup taken before a shred stays readable while the key that wrapped it exists. Destroy an old key, in configuration **and** in Key Vault including soft-deleted versions, only when both are true: no live document uses it, and the backup retention window has passed since the last of its documents was shredded. Record the destruction in the change log. Removing a key early makes its documents unreadable, the same as shredding them.
- The host refuses to start if a configured key is not 32 bytes of base64, or if `ActiveKeyId` names no configured key.
- **Suspected key leak:** rotate as above at once, and escalate to security and privacy. Documents wrapped with the leaked key stay readable to whoever holds it until they are shredded.

## Diagnose
All queries are read-only.
1. The purge job: `customers.JobLeases` for `customers.purge-personal-data` (last holder and expiry), and the Worker logs.
2. Sets due but not purged: `customers.TravellerSets` with `LegalHold = 0` and `AnonymisedAt` null and `RetainUntil` in the past, or with live documents past `DocumentsRetainUntil`.
3. What happened to a set: `customers.RetentionEvents` by `OrderId` (holds, shredding, anonymisation, with actor and reason) and `customers.DocumentAccessLog` by `DocumentId` (every store, read and shred, with the correlation id). Neither ever contains personal data.
4. The review list (Q10): customers with at least `Payments:AttemptLimits:AlertAfterTrips` refusals (default 3) in 24 hours, from `payments.AttemptLimitTrips` grouped by `CustomerId`. The admin view comes with staff identity. Until then, query read-only.

## Resolve
### Purge stopped
Check that the Worker is running with the Customers connection string. A failing run leaves everything as it was, and the next run catches up, because the purge is idempotent. There is no manual purge: fix the Worker.

### Legal hold (litigation, dispute, authority request)
Holds are placed on instruction from legal only, with a ticket reference as the reason (never personal data). A held set is neither purged nor changed. Releasing a hold lets the next purge run apply any overdue retention.

**Not yet operable.** There is no operator entry point for `LegalHoldHandler` until staff identity exists (ADR 0008), and editing `LegalHold` in the database is not allowed, because it skips the record. Until the entry point ships (a production gate), escalate a hold request to engineering at once. Engineering first stops the Worker's purge job, which is the only thing that shreds or anonymises. Then a reviewed change applies the hold through `LegalHoldHandler`.

### `PaymentAttemptLimitRepeated`
A customer reached the payment-attempt limit repeatedly: possible card testing. Look at their attempts (`payments.PaymentAttempts` by `CustomerId`) and at Stripe Radar for the same payments. There is no automatic block in the MVP (Q10). Decide with fraud operations, and record the outcome in the ticket.

## Do NOT
- Do not decrypt or export documents for investigation. Every read is audited, and none is needed to diagnose.
- Do not update or delete `RetentionEvents`, `DocumentAccessLog` or `AttemptLimitTrips` rows. They are append-only evidence.
- Do not change retention dates or `LegalHold` in the database by hand.
- Do not raise the attempt limits for one customer. The limits are global configuration.

## Escalate
Privacy/legal for holds, erasure requests and anything touching the retention periods (the final financial retention period is for counsel, Q14). Security for key incidents. Fraud operations for the review list.
