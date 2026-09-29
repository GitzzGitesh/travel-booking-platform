# 0020. Traveller personal-data store, document encryption and retention (Q9)

- **Status:** Accepted (2026-09-29), as written, with its production caveats (Key Vault key wrapping recommended before production; legal hold not operable until staff identity exists). The policy (what is collected, retention periods, erasure approach) was approved on 2026-09-28 (Q9), and the abandoned-order rule in decision 6 on 2026-09-29.
- **Date:** 2026-09-29
- **Deciders:** Project owner
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0007](0007-async-processing-worker-and-outbox.md), [0008](0008-identity-and-permissions.md), Q9 and Q10 in `docs/requirements/open-questions.md`, `docs/architecture/security.md`, `docs/runbooks/personal-data-retention.md`

## Context

Supplier booking needs traveller names, dates of birth and gender, and a booker contact. Some offers also need a passport or identity document. Q9 approved this set and nothing more. It also approved the retention rules: documents are kept until the last flight segment plus 30 days, and traveller and booker data until the last segment plus 25 months, then anonymised. The erasure approach was also approved: a separate personal-data store keyed by internal ids, anonymised booking and financial records, crypto-shredded document keys, a scheduled purge and a legal hold. The final financial retention period, invoice-name rules, cross-border transfer basis, minors and per-market document rules are for counsel (Q9, Q14).

## Decision

1. **Personal data lives in the Customers module** (schema `customers`), keyed by the internal order id and customer id: `TravellerSets` (booker email and phone, retention dates, legal hold), `Travellers` (passenger type, names, date of birth, gender) and `TravelDocuments`. Orders, payments and timelines hold no names or contacts. The customer gives the travellers per order (`PUT /api/v1/orders/{orderId}/travellers`), only while the order awaits payment, for exactly the order's passengers.
2. **Documents are collected only when the supplier says so.** The supplier's revalidation sets `DocumentsRequired` on the offer (Amadeus: `bookingRequirements.travelerRequirements[].documentRequired`; mock: destination `ZDR`). It is copied to the order item and refreshed at checkout. A document is accepted only then (`409 documents-not-required` otherwise), and is never returned by the API.
3. **Envelope encryption per document.** Each document gets a random 256-bit data key. The document is sealed with AES-256-GCM, with the document id as associated data, so a ciphertext cannot be moved to another row. The data key is wrapped with AES-256-GCM under a key-encryption key (KEK) named by `KeyId`. KEKs come from configuration (`Customers:DocumentEncryption:ActiveKeyId` and `Keys:{id}`, base64 of 32 bytes), which means user-secrets locally and a Key Vault **secret** in Azure. There is no default: without a key no document is stored (`503 documents-unavailable`). Old KEK ids stay configured while documents wrapped with them exist, which allows rotation.
4. **Crypto-shredding.** Shredding a document deletes its wrapped key and ciphertext and keeps the row (id, order, traveller, times) as evidence. A document is shredded when its traveller's details change, when it is replaced, when its retention date passes, or when the set is anonymised. **Backups:** row shredding does not reach database backups or exports made earlier, and they stay readable while the KEK that wrapped them exists. So KEKs are the unit of shredding for copies. A new KEK becomes active every month, and a retired KEK is destroyed once every document it wrapped is past its retention **plus the backup retention window** (runbook). Key Vault key wrapping (below) makes this enforceable, and is recommended before production.
5. **Audited access.** Every store, read and shred appends a `DocumentAccessLog` row (actor, time, correlation id, never the data). A read releases the plaintext only after its audit row is saved. Retention actions and legal holds append `RetentionEvents`.
6. **Retention and purge.** Each set stores `RetainUntil` (last travel date + `Customers:Retention:PersonalDataMonthsAfterTravel`, default 25) and `DocumentsRetainUntil` (+ `DocumentDaysAfterTravel`, default 30). The last travel date is the last segment's local arrival date. The Worker job `customers.purge-personal-data` (hourly, under a lease) shreds due documents and anonymises due sets. Anonymising clears names, date of birth, gender and contact, and keeps passenger types and counts so booking and financial records still add up. It is idempotent.
   **Abandoned orders** (never booked; approved 2026-09-29):
   - When the order is abandoned, its documents are shredded at once, and its traveller and booker data is anonymised 30 days after abandonment (`Customers:Retention:PersonalDataDaysAfterAbandonment`).
   - Orders publishes `OrderAbandoned` through its outbox in the same save. Customers consumes it once through its inbox, shortens the set's dates (never lengthens them) and runs the purge for it.
   - The expiry job abandons an order only when no payment attempt for it is unsettled (Pending or unknown, ManualReview, Authorized or ActionRequired being released). While any is unsettled, the normal retention applies.
   - A legal hold still blocks the purge.
   - Booked orders keep the normal rule.
7. **Legal hold.** A held set is neither purged nor changed, including by a purge that loaded it just before the hold: the purge saves through the set's row version. Placing or releasing a hold names the operator and a reason, and is recorded. **Not yet operable:** there is no operator entry point until staff identity exists (ADR 0008). That entry point is a production gate.
8. **New project `Modules.Customers.Contracts`.** Orders reads traveller readiness through `IOrderTravellers`, and Customers reads the order's passenger needs through `IOrderTravellerNeeds` (Orders.Contracts), as queries only (ADR 0002). It also holds the traveller endpoints' public value types. Checkout refuses to authorize payment until the travellers, and any required documents, are complete, and checks again just before booking. Travellers are frozen while a payment attempt is live.

## Alternatives considered

- **SQL Server Always Encrypted:** it needs the column master key in the client driver's key store, and makes per-document shredding and rotation harder. It may be revisited with hosting (Q2).
- **Key Vault key wrap/unwrap (HSM keys):** stronger, because the KEK never leaves the vault. It needs the Key Vault SDK (a dependency) and a hosting decision. The `IDocumentProtector` port lets it replace the configuration KEK without changing callers. This is recommended before production.
- **Storing traveller data on the Order:** rejected. It would spread PII into the orders schema and timelines, and make erasure a multi-module change.

## Consequences

- Erasing a customer's personal data is local to the Customers schema. Account deletion itself (the customer mapping) is still to come.
- Anyone with the configuration KEK and database access can decrypt documents. Production needs Key Vault access policies, and preferably the key-wrap option above.
- The final financial retention period and per-market document rules are not encoded. The purge covers personal data only. Financial records follow Q14 when counsel answers.
