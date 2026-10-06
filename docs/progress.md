# Progress

_Last updated: 2026-10-02 (Phase 3 complete with mock providers; Phase 4: identity and admin foundation, in progress)_

## Current phase: 4 — Identity & admin foundation, in progress (Phase 3, the first vertical slice, is complete with mock providers: row 5 below)

**Q1 answered 2026-09-25: we are merchant of record for flights (Option A).** **Q8 answered 2026-09-26: customers sign in before booking (no guest checkout).** **Q6 answered 2026-09-27: Amadeus is the first production flight supplier (ADR 0019), not yet credentialed or verified.** Hotels (Q1), markets (Q2), currencies and FX (Q5), retention (Q9), fraud (Q10), refund thresholds (Q11) and group or child-only bookings (Q13) stay open. **ADR 0005 was accepted for flights**, since its Q1 gate is now met. **ADR 0006 stays Proposed**: accepting it also fixes the payment provider (Stripe), which depends on Q2 and Q5.

### Phase 3: plan
| # | Chunk | Status / depends on |
|---|---|---|
| 1 | **Orders foundation**: the `Modules.Orders` module (schema `orders`) and `Modules.Flights.Contracts` | **Done.** An `Order` aggregate with `FlightOrderItem`s and an explicit item state machine up to the booking outcome (`AwaitingPayment` → `Booking` only with a payment authorization reference; `PendingConfirmation`, `ManualReview` for a supplier mismatch, `Confirmed` with the supplier locator, `Failed`, `Abandoned`). Illegal transitions return errors; re-applying the same transition is a no-op, and a conflicting reference is an error. It has an append-only `OrderTimeline` (actor, time, from → to, reason, correlation id), and the order status is derived from its items. `CreateFlightOrderHandler` creates an order only from a Flights selection that is revalidated, `Confirmed` and unexpired, read through `IFlightSelections` (Contracts: the agreed price and expiry, never the supplier token). It is idempotent by key (unique), with at most one order item per selection (unique). A `Revision` counter forces the rowversion check on every item change. Migration `InitialOrders`. **No endpoint and no payment calls**: order creation over HTTP needs the guest-checkout decision (Q8), and payments need ADR 0006 |
| 1b | Reviews of chunk 1 addressed before shipping the schema | **Done.** The timeline records a provider reference (the payment authorization, or `provider:locator`). The payment authorization is on the **Order** (ADR 0005), and `StartBooking` moves all items at once. Booking is refused once an item's offer has expired (F-02). A card decline keeps the item `AwaitingPayment` for another attempt (F-20); `Abandon` means no authorization is outstanding, and an authorization timeout is an unknown payment outcome, not `Abandoned`. Consent evidence is kept: the accepted quote id and acceptance time (Flights migration `AddPriceAcceptedAt`) are snapshotted on the order item |
| 2 | **Provider-neutral payment port and deterministic mock** (the decision of 2026-09-26: ADR 0006 is **not** accepted, and the real provider waits for Q2 and Q5) | **Done.** `Modules.Payments` (ADR 0004) has `IPaymentProvider`: authorize (manual capture), capture, void, refund, and a lookup by our `PaymentReference`. It is idempotent by our keys (payment reference; `OperationKey` for later writes), with an `IdempotencyConflict` for a key reused with other details. The payment-method token is opaque and refuses card-number-shaped values (SAQ-A). `PaymentOperations` makes exactly one write per call and gives distinct outcomes: `Authorized`, `ActionRequired`, `Declined` (with a reason), `Captured`, `Voided`, `Refunded`, `Rejected`, **`Unknown`** (timeouts, Unavailable, RateLimited or AuthFailure on a write, exceptions), `NotFound` as of an instant, and `Mismatch`. `Integrations.Payments.Mock` (Development and Staging only) has scenario tokens for approved, declined, insufficient funds, SCA required, the two authorization timeouts, capture timeout, refund unavailable once and refund pending. After the booking-flow, architecture and security reviews:
- The payment port returns the shared `ProviderErrorKind` (ADR 0004/0014), extended with `IdempotencyConflict` and `OperationInProgress`; a decline is a payment state.
- There is one capture per payment. Refunds are records with status and a lookup by our key. A reference is one attempt, so another card after a decline gets a new reference.
- There are `Canceled` and `Expired` states, and a replayed authorization is classified by the actual state.
- The card guard strips separators and checks any 13–19-digit group with Luhn, and the customer action token is redacted. `PaymentProviderContract` is the suite every future adapter must pass. **Nothing is persisted and there is no endpoint**; Orders is unchanged, since `StartBooking` already requires the order's authorization reference |
| 3 | **Checkout foundation** (one batch: Q8, payment attempts, checkout payment step; no endpoint) | **Done.** Q8 answered 2026-09-26 (sign-in required before booking, no guest checkout); the identity provider stays open (ADR 0008 Proposed).
- **Orders belong to a signed-in customer:** `Order.CustomerId`, unique `(CustomerId, IdempotencyKey)`, owner-scoped lookups (another customer's order is not found), actor `customer:{id}` on the timeline (migrations `AddOrderCustomer`, `WidenTimelineActor`).
- **Payment attempts** (`Modules.Payments.Contracts`: `IOrderPayments`; schema `payments`, migration `InitialPayments`): saved as Authorizing before the provider call; unique per order and key; **one live attempt per order** (filtered unique index, F-32); unknown, crashed or challenged attempts are looked up by our reference, never re-authorized; "not found" is conclusive only after `Payments:Reconciliation:NotFoundConclusiveAfter` (15 minutes by default); amount mismatches and later-phase states go to ManualReview; history with actor and correlation id; token and customer action never printed.
- **Checkout payment step** (`AuthorizeCheckoutHandler`, Orders): finish an attempt already made with the key first; otherwise revalidate every item with the supplier now (`IFlightSelections.RevalidateAsync`), adopt a new expiry and, only with a newly accepted quote, a new price (F-01); refuse offers with under two minutes left; authorize the server-side total; `Booking` only on an authorization of exactly that total. An authorization the order will not book on is noted on the timeline for release (F-22). |
| 4 | **Background money safety** (one batch, ADR 0007) | **Done.** Worker jobs under DB leases, over a BuildingBlocks outbox, inbox and lease store in each module's schema. Payment attempts are reconciled by lookup (Authorizing, AuthorizationUnknown, ActionRequired), and holds Orders will not use are voided once, keyed by the attempt (`Voiding`, `VoidUnknown`, `Voided`; F-21, F-22), after an `OrderPaymentReleaseRequested` event (Orders outbox → Payments inbox). Orders whose offer expired are abandoned only when no live payment attempt remains (F-02). Migrations `AddOutboxAndJobLeases` (orders) and `AddHoldReleaseAndInbox` (payments). Runbook `payment-hold-release.md` |
| 4b | **Flights model readiness** (audit batch 2): fare model enrichment, provider resolution by id, airport reference data | **Done.** See "Phase 3: flights model readiness" below |
| 4b-q6 | **Flight supplier preparation (Q6)**: Amadeus, Sabre, Travelport and Duffel adapters | **Done (technical preparation only, ADR 0018).** Capability model and adapter stages; startup composition rules (a search provider must implement search; only ProductionReady adapters outside Development and Staging); operations an adapter does not implement are never called. Duffel and Amadeus search and revalidation are mapped from documentation (fixture-tested); Sabre and Travelport are scaffolds. Booking is implemented for none; sandbox contract tests are credential-gated. Q6 itself stays open: see `docs/architecture/flight-suppliers.md` |
| 4b-amadeus | **Q6 answered: Amadeus first (ADR 0019)** | **Done (decision and safe adapter work).** Amadeus is the first production supplier; we are merchant of record; Sabre, Travelport and Duffel stay future integrations. Adapter: the optional `Currency` setting (`currencyCode`), and numbered Amadeus error codes recorded on failures (never supplier text; no code changes an error kind until sandbox-confirmed). Still MappedFromDocumentation: no credentials, no sandbox run, no booking. Remaining requirements R1–R13 in ADR 0019 |
| 4c | **Customer identity and the customer order path** (ADR 0008 foundation) | **Done (foundation).** `Modules.Customers` (schema `customers`, migration `InitialCustomers`): customer bearer tokens validated with JwtBearer (signature, issuer, audience, lifetime, RS256 only), and the account (issuer and user object id `oid`) mapped to one internal customer id (unique constraint, safe on concurrent first sign-in), added as a claim. Orders, payments and timelines use only that id. Endpoints (Development gate, like flights): `GET /api/v1/customers/me`, `POST /api/v1/orders` (Idempotency-Key; only the selection id is sent) and `GET /api/v1/orders/{id}` (the owner's orders only; others are not found). customer-web: a `CustomerSession` boundary, always signed out, and "Sign in to book this flight" after a confirmed price. **Blocked:** a real Entra External ID tenant (`Authentication:Customers:Authority`, `Audience`, optional `RequiredScope`: placeholders, so every customer token is refused until set); the customer-web token pattern (MSAL or BFF, decided with the tenant); Q9 for any contact or traveller data; the checkout preconditions below |
| 4d2 | **Checkout preconditions** (selection ownership, replay semantics, attempt cap, generic declines, the way out of ManualReview) | **Done.**
- **Selection ownership:** a flight selection records the signed-in customer (internal id, from a validated token only; migration `AddSelectionOwner`). Selections are unique per search offer **and owner**, so nobody can block or see anyone else's, and a customer who browsed anonymously simply selects again once signed in.
  - Only its owner may revalidate it, accept its price or order it.
  - An anonymous selection can be price-checked but never ordered.
  - Anyone else gets exactly what an unknown selection gets: 404 `selected-offer-not-found`, or 422 `offer-expired` when ordering.
  - A bearer token sent to the selection endpoints must be valid, never silently anonymous: otherwise 401 with `WWW-Authenticate: Bearer` and a `CustomerTokenRejected` security event.
  - Orders created before this change hold ownerless selections and can no longer be paid (Development data only).
- **Replay semantics:** see api-design rules. A replay returns 200 with the same order (the same body while nothing changed), even from a new process: the key lives in the database.
- **Attempt cap:** `Payments:AttemptLimits` (per order 5, per customer in 24 hours 20: defaults only, business-configurable, Q10), counted from stored attempts (index `(CustomerId, CreatedAt)`, migration `AddAttemptLimitsAndVoidGeneration`), with a `PaymentAttemptLimitReached` security event. Replays of existing attempts are never limited.
  - The per-order cap is exact, because one live attempt per order is enforced by a unique index.
  - The per-customer cap is **approximate**: parallel checkouts across several orders all pass the count before any insert.
  - The real controls are a per-customer request rate limit on checkout (a precondition below) and the provider's fraud tooling (Q10). A hard cap would need a per-customer lock or counter row.
- **Generic declines:** the decline reason no longer crosses `Payments.Contracts`. It stays on the attempt and its history (operations). Checkout exposes `CustomerPaymentState` only: Accepted, ActionRequired, Declined, Pending, Unavailable (a manual review is never named).
- **ManualReview:** `ResolvePaymentReviewHandler` (no endpoint until staff identity). It looks the payment up and moves the attempt only to what the provider holds (Authorized, or a status holding nothing), idempotently, with `operator:{id}`, the reason and the correlation id on the history.
  - The provider payment the lookup found is recorded; Authorized is refused without it.
  - A payment the provider once had and no longer finds stays in review. "Not found" is Failed only when the provider never acknowledged it, after the consistency window.
  - A new void generation starts, so a void refused before the review is sent afresh.
  - Anything unsettled stays in review, and the check is recorded.
  - A release the reconciler cannot void raises `PaymentHoldNotReleasable`.
  - The booking-item equivalent needs supplier lookup, and comes with booking orchestration.
  - **Gates for its admin endpoint:**
    - the operator id taken from the staff principal (Entra `oid`), never from the request;
    - an audit record with before and after status;
    - a permission (e.g. `payments.review.resolve`) with MFA;
    - a rate limit (each check appends history);
    - reasons as ticket references with no personal data.
  - An operator-triggered void of a hold of another amount is a follow-up for the operations batch (today: escalate) |
| 4f | **Traveller data, retention and fraud controls** (Q9 and Q10, answered 2026-09-28; ADR 0020 Proposed) | **Done.**
- **Travellers and contact:** `PUT/GET /api/v1/orders/{orderId}/travellers` (owner only; others get 404). One traveller per passenger of the order (type, Latin-letter names, date of birth checked against the passenger type on the last travel date, Female/Male), plus the booker's email and E.164 phone. Changes are allowed only while the order awaits payment and no legal hold applies. Nothing else is collected. The order item now records its passengers and `DocumentsRequired` (migration `AddTravellerNeeds`) and returns them as `travellers` on the order (additive).
- **Documents only when the supplier requires them:** revalidation sets `DocumentsRequired` (Amadeus `bookingRequirements.travelerRequirements[].documentRequired`, mock destination `ZDR`; Flights migration `AddDocumentsRequired`), and checkout refreshes it and records a change on the timeline. `PUT .../travellers/{position}/document` returns 409 `documents-not-required` otherwise. A document is never returned.
- **PII boundary (Customers schema, migration `AddPersonalData`):** traveller sets, travellers, documents, the document access log and retention events. Each document is sealed with its own AES-256-GCM data key, wrapped by a key-encryption key from configuration (`Customers:DocumentEncryption`, user-secrets or Key Vault; no key means no document is stored, 503). Every store, read and shred is audited. A traveller whose details change loses their document (crypto-shredded).
- **Retention:** `RetainUntil` = last travel date + 25 months and `DocumentsRetainUntil` = + 30 days (`Customers:Retention`). The Worker job `customers.purge-personal-data` (hourly) shreds and anonymises idempotently, keeping passenger types for the records. **Legal hold:** `LegalHoldHandler` (operator and reason recorded; no endpoint until staff identity) freezes a set against purge and changes.
- **Checkout gate:** `AuthorizeCheckoutHandler` refuses payment until the travellers (and any required documents) are complete. It checks again just before booking on every path, including a payment resumed after a challenge; if the check fails, the hold is released and nothing is booked. Travellers are frozen while a payment attempt is live (declined attempts free them). It reads readiness through `Modules.Customers.Contracts` (`IOrderTravellers`), and Customers reads the order's needs through `IOrderTravellerNeeds` (queries only). An order made before this change has no recorded passengers and is never booked (Development data only).
- **Q10:** attempt limits 5 per order and **10** per customer in 24 hours. Each refused request is recorded once per idempotency key (`payments.AttemptLimitTrips`, migration `AddAttemptLimitTrips`), and the refusal stands even if recording fails. When a customer reaches `AlertAfterTrips` (3) in 24 hours, the `PaymentAttemptLimitRepeated` alert fires once and the customer goes on the review list (`PaymentAttemptReviewList`; admin view with staff identity; no automatic block). Rate limits: the per-address limits still run **before** authentication, so bad-token floods are limited. A **per-customer write limit** `RateLimiting:Customer` (30/min, sized for a 9-passenger booking with documents; keyed by internal customer id; an endpoint filter after authorization) applies to POST/PUT on the order and traveller endpoints. It will apply to checkout when checkout is exposed. Reads (status polling) are limited by address only.
- **Not done (deliberately):**
  - The checkout HTTP endpoint: it would hold funds with no supplier booking behind it, so it comes with booking orchestration (row 5).
  - Account deletion.
  - Stripe Radar rules and card-fingerprint velocity (Stripe account setup, ADR 0006).
  - Staff endpoints for legal hold and the review list. **Legal hold is not operable until then: a production gate.**
  - Shredding documents when the supplier stops requiring them (they are shredded 30 days after travel).
  - Retention of `AttemptLimitTrips` (security audit, 12 months).
  - KEK rotation through Key Vault key wrapping (recommended before production; row shredding does not reach backups, see ADR 0020).
- **Abandoned orders (approved 2026-09-29):**
  - When the order becomes Abandoned (`OrderAbandoned` through the Orders outbox, Customers inbox, migration `AddCustomersInbox`), its documents are shredded at once, and its traveller and booker data is anonymised 30 days later.
  - An order is abandoned only when no payment attempt is unsettled, so unsettled payments keep the normal retention.
  - A legal hold blocks the purge.
  - Booked orders keep 25 months after travel.
- ADR 0020 **Accepted** 2026-09-29.
- **To confirm per supplier:** ages are checked against the last travel date; some carriers use the age at first departure for children and adults. Runbook `personal-data-retention.md`. **Counsel items (Q9):** the final financial retention period (Q14), invoice-name requirements, the cross-border transfer basis, minors, and per-market document rules |
| 4e | Supplier booking then capture | Superseded by row 5 (built with the mock supplier) |
| 4d | **Payment provider groundwork (ADR 0006, revised, Proposed)** | **Done (not production-ready).** Recommendation: Stripe, with server-side confirmation (a browser-created token is confirmed by our server; the challenge runs in the browser; its result is learnt by lookup). The port gains `IsProductionReady` (fail closed, enforced at startup), a lookup by known provider payment id, and `IPaymentNotifications`. Webhook intake: `POST /api/v1/payments/notifications/{providerId}`, verified, one row per event (migration `AddPaymentNotifications`), processed by a Worker job that looks the payment up. `Integrations.Payments.Stripe`: plain HTTP, test keys only, USD/EUR/GBP (INR once confirmed, TND refused), fixture-tested; the shared contract runs against Stripe test mode only with credentials (skipped otherwise). Mock: completed and failed challenge scenarios. **Needs:** the owner's acceptance of Stripe, Q14 and P1–P10 in ADR 0006. **Deviation to confirm:** notifications are stored without the raw event |
| 5 | **Booking orchestration** (ADR 0021, Accepted 2026-09-30): authorize → book → capture | **Done, with the mock supplier.**
- **Checkout endpoint:** `POST /api/v1/orders/{orderId}/checkout` (customer policy, Idempotency-Key, the provider's payment token; the amount comes from the order). It revalidates, checks the travellers, authorizes, then books each item through `IFlightBookings` (Flights.Contracts, only Orders may call it), with the travellers read for this booking only (audited). It answers 200 (Booked, BookingFailed with the hold released, Declined, ActionRequired) or 202 (BookingPending, PaymentPending). A repeat never holds or books twice.
- **Outcomes:** Booked → `Confirmed` with the PNR and ticketing state (shown on the order as `bookingReference` and `ticketing`); NotBooked → `Failed`; Unknown → `PendingConfirmation`; Mismatch → `ManualReview`.
- **Unknown bookings:** the Worker job `orders.reconcile-bookings` looks them up by our reference and never books (`Orders:BookingReconciliation`: interrupted after 5 min, not-found conclusive after 15 min, a person after 24 h, with alerts).
- **Money:** capture only after a confirmed booking. The order's settlement (the charge for confirmed items, or the release when none is) goes through the outbox in the same save (F-25). Payments captures once with `{attempt}:capture` (`Capturing` / `CaptureUnknown` / `Captured`; a failure goes to `ManualReview` with `PaymentCaptureFailed`). Migrations `AddBookingOrchestration` (orders) and `AddPaymentCapture` (payments).
- **Fixed on the way:**
  - A failed concurrent save now clears the Orders unit of work, so a retry reads the stored order, not this request's unsaved changes (and outbox rows).
  - The Payments database context now retries transient SQL errors, as Orders and Flights already did. Parallel same-key authorizations could make a read the deadlock victim (SQL error 1205), which surfaced as an error instead of a replay.
  - (2026-10-02) Orders are no longer read torn. The order, its items and its timeline are separate queries, so a parallel checkout committing between them gave items in Booking on an order without its authorization, and a same-key duplicate was answered 409 `order-not-payable` (seen once in CI). The store now repeats a load whose order rowversion moved while it was reading (every change rewrites the order row). After 5 tries it gives up: checkout answers 503 try-again, or 202 pending once booking has started (never a 500 for a booked flight). Tested with a change committed mid-load, once and every time.
- **Not done:** Amadeus booking (ADR 0019: its booking ADR, sandbox and supplier answers; checkout refuses its offers before payment); ticketing and fulfilment after confirmation (F-16); cancellation and refunds; operator endpoints for booking and payment review (staff identity); the F-24 re-authorize-or-cancel policy (business); the checkout UI; multi-item orders (F-17 end to end); the operator resolution of an item in manual review. It must settle the payment in the same save; until then, a held payment waits for a person and lapses after about 7 days (runbook); telling a traveller-read failure at booking that could pass on retry from a lasting one (today nothing is sent and the hold is released: safe, but the customer must pay again). Runbooks `booking-pending-confirmation.md` and `payment-captured-booking-failed.md` |
| 6 | **Phase 4 start: staff access and first operations** (ADR 0022, Accepted 2026-10-01) | **Done, without a staff tenant.**
- **Access module:** new module `Modules.Access`, schema `access`, migration `InitialAccess`.
  - The `Staff` JwtBearer scheme, configured by `Authentication:Staff`. Placeholders mean every staff token is refused.
  - Tokens must show MFA (`amr` contains `mfa`). Reserved claims and app-only tokens are refused.
  - A staff account maps to an internal staff id.
  - Code-defined roles (Operations, Administrator) are bundles of permissions, granted by `Access:RoleAssignments` (nobody by default).
  - `staff:{permission}` policies; a 403 is the `StaffAuthorizationDenied` security event.
- **Audit log:** BuildingBlocks `AuditEntry`, in each module's own schema, saved with the action. Orders gets one (migration `AddOrdersAuditLog`).
- **Admin routes** under `/api/admin/v1`: Development-gated, rate limited, and out of the customer OpenAPI document (group `admin-v1`).
  - `GET /orders?itemStatus=ManualReview|PendingConfirmation|Booking&limit&cursor` and `GET /orders/{id}` (with the timeline), permission `orders.read`.
  - `POST /orders/{id}/items/{itemId}/review-checks` (`bookings.review.resolve`): settled only by a supplier lookup, with the payment settled and the action audited in the same save. This closes the deferred operator path from row 5.
- **Review fixes:**
  - A mismatched booking is never failed by a later "not found" (it stays in review, never charged), and its policy is Q15.
  - Failed lookups stay in review, and conflicted checks are audited.
  - The repeat answer is 409 with the current status.
  - The reason is limited to a ticket-reference pattern.
  - Composite queue cursor, and the supplier-call rate limit on the check.
  - Refused staff tokens are security events.
  - The MFA signal is configurable (`amr` or a CA authentication context in `acrs`).
  - Role grants are read live.
  - `Modules.Access.Contracts` holds the permission catalog.
  - The admin OpenAPI document is separate.
- **Next:** done in row 7.
- **Blocked externally:** the staff tenant (Entra ID workforce) and its token claims (`amr`). |
| 7 | **Staff operations, part 2** (ADR 0022, Accepted 2026-10-01) | **Done, without a staff tenant.**
- **Payments:**
  - `POST /api/admin/v1/payments/{attemptId}/review-resolutions` (`payments.review.resolve`): settled only by a provider lookup (F-26). A repeat answers 409 with the current status. Supplier-call rate limit.
  - `GET /api/admin/v1/payments/{attemptId}`: the attempt and its history.
  - `GET /api/admin/v1/payments/attempt-limit-reviews`: the Q10 review list. Both need `payments.read`.
- **Personal data:** `PUT /api/admin/v1/orders/{orderId}/legal-hold` (`personal-data.legal-hold`): idempotent by state, recorded as a retention event (ADR 0020).
- **Common to all three:** every change is audited in the acting module's schema, in the same save (migrations `AddPaymentsAuditLog`, `AddCustomersAuditLog`). Staff actors are `staff:{id}` everywhere.
- **Roles:** Operations gains the payments permissions; a new Privacy role holds the legal hold (with `orders.read`).
- **Reasons:** one rule for staff reasons (`AuditReasons`): a ticket reference or a plain note.
- **Next:** managed role grants with maker-checker, and the `admin-web` shell (its token pattern, BFF lean, is to be finalised).
- **Blocked externally:** the staff tenant. |
| 8 | **Managed staff role grants with maker-checker** (ADR 0022) | **Done.**
- **Requests:** `POST /api/admin/v1/access/role-changes` (`access.grants.request`) for a workforce object id, a role, Grant or Revoke, and a ticket reference.
- **Decisions:** a different administrator decides with `POST .../{id}/decision` (`access.grants.approve`). Nobody requests a change to their own access or approves their own request.
- **Uniqueness:** one pending request per account and role, and one active grant (filtered unique indexes; migration `AddRoleGrants`).
- **Effective permissions:** the configuration bootstrap plus approved grants, read on every sign-in (revocation is immediate).
- **Reads:** `GET .../role-changes` and `GET .../role-grants` (`access.grants.read`).
- **Audit:** every request and decision in `access.AuditEntries`.
- **Review fixes:**
  - The account a change concerns never decides on it, either way, and a revocation can only be withdrawn by its requester.
  - Requester and approver are also compared by account.
  - Approval needs a requester who may still request, a request under 7 days old, and a real change.
  - Configuration-granted roles answer `granted-by-configuration` and are listed with their source.
  - Object ids must be canonical lowercase GUIDs.
  - The request list is cursor-paged.
  - Maker-checker refusals are `RoleChangeRefused` security events.
- **Runbook:** `staff-access.md`.
- **Next:** the `admin-web` shell (row 9).
- **Blocked externally:** the staff tenant. |
| 9 | **admin-web staff session and operations shell** (ADR 0023, Accepted 2026-10-01: option A) | **Done, without a staff tenant.**
- **Backend-for-frontend in the Api host:** staff sign in with OpenID Connect on the server (code flow, PKCE, query response mode, no tokens saved) into the `__Host-tb-staff` cookie (HttpOnly, Secure, SameSite=Strict; 30 minutes idle, 8 hours absolute).
  - The session holds the account and sign-in time only. Every request maps the staff member and permissions again, so revocations still apply at once.
  - Unsafe requests need `X-TB-Staff-Csrf: 1`.
  - Admin policies accept the staff token or the session, never both.
  - New package `Microsoft.AspNetCore.Authentication.OpenIdConnect` (Microsoft, MIT), approved with the ADR.
- **Endpoints:**
  - `GET /api/admin/v1/session` (`staff:signed-in`)
  - `GET .../session/sign-in` (anonymous; local return paths only; 503 `staff-sign-in-unavailable` until a tenant is configured)
  - `POST .../session/sign-out`
  - `POST .../session/development-sign-in`: Development only, behind `Authentication:StaffSession:DevelopmentSignIn`; startup refuses the setting elsewhere.
- **Staff API contract:** `openapi.admin-v1.json` (now staff routes only; before, it also listed the customer routes) is enforced like the customer contract. It is generated into `projects/admin-api-client`, which only admin-web may import (boundary check).
- **admin-web:** sign-in state, navigation by permission, the booking queues (manual review, awaiting supplier confirmation, booking in progress; cursor paging) and the order page with its items and timeline (read-only), with the CSRF header and session-ended handling. Dev proxy to the Api.
- **Tests:**
  - Api: `StaffSessionTests` (cookie flags, permissions, CSRF, live grants, ambiguity, sign-out, idle and absolute lifetimes, return paths), plus authorization and contract tests.
  - Vitest: session, interceptor, shell and queue.
  - Playwright: sign in, queues, sign out, with axe. CI applies the Orders and Access migrations for it.
- **Next:** admin-web operations actions (row 10).
- **Blocked externally:** the staff tenant's app registration (redirect URI `/api/admin/v1/session/callback`, a client credential in Key Vault); the MFA claim shape in ID tokens; the Data Protection key ring and the admin-web CSP and headers (hosting, Q2). |
| 10 | **admin-web operations actions** (ADR 0022, ADR 0023; UI only, no API change) | **Done, without a staff tenant.**
- **Order page:** for an item in manual review, **Check with the supplier** (`bookings.review.resolve`), with the outcome announced: settled, still in review, or no longer in review. **Place / Release legal hold** (`personal-data.legal-hold`). The payment id links to the payment page (`payments.read`).
- **Payment page** (`/payments/{attemptId}`, `payments.read`): amounts, provider references and the history. A payment in manual review offers **Check with the payment provider** (`payments.review.resolve`): settled only by the provider's answer (F-26).
- **Payment attempt reviews** (`payments.read`): the Q10 review list.
- **Staff access** (`access.grants.read`): roles held (with their source), requests waiting for a decision (**Approve** / **Reject**, `access.grants.approve`), and **Request a change** (`access.grants.request`; a lowercase GUID and a known role). The server's maker-checker refusals are explained on the page.
- **Every action** takes a ticket reference, checked against the server's rule before sending; one action at a time, so a double click is one request. Navigation and routes follow the permissions; the server checks every call.
- **Tests:** Vitest for the order, payment and access pages (actions, refusals, permissions); Playwright with axe for the payment review list and staff access, including a self-approval refused in the UI. E2E now also applies the Payments migrations.
- **Next:** row 11. |
| 11 | **Decisions (2026-10-02, delegated authority) and Batch A: customer notices and payment hold deadlines** | **Done; the email provider is external.**
- **Decisions:** ADR 0024 (notifications, ACS Email as the provider), ADR 0025 (resolving bookings in review, Q15), ADR 0026 (legal-hold release maker-checker, Q16), ADR 0027 (refunds and cancellations, Q11). Each records what is pending legal or provider confirmation.
- **Notifications module** (`Modules.Notifications`, schema `notifications`, migration `InitialNotifications`; Worker only):
  - Orders publishes `OrderBookingSettled` in the same save as the payment settlement (capture or release). Its outcome is Confirmed, PartiallyConfirmed or NotBooked, with the booked references and the amount charged; no personal data.
  - A notice is recorded once per event and kind (unique constraint).
  - The `notifications.send` job sends at least once: saved as Sending first, unknown outcomes retried with backoff, and `NotificationFailed` after 10 attempts.
  - The recipient is read from Customers (`IOrderContacts`) when sending and never stored. An anonymised order is suppressed.
  - Typed HTML and text templates, English first.
  - The `IEmailSender` port has a deterministic recording sender for Development and Staging. With no provider configured, notices wait safely (`NotificationProviderMissing`).
- **Payment hold deadlines:** `AuthorizedAt` (backfilled from each attempt's history) and `HoldWarningRaisedAt` (migration `AddPaymentHoldDeadlines`).
  - The `payments.watch-expiring-holds` job raises `PaymentHoldExpiring` once per held attempt, `Payments:Holds:WarningBefore` (48 hours) before `Lifetime` (7 days; a placeholder until the provider confirms it).
  - The staff payment API and page show "Hold lapses" (additive `authorizedAt`, `holdExpiresAt`).
- **Runbook:** `customer-notices-and-hold-deadlines.md`.
- **Next:** row 12.
- **Blocked externally:**
  - the ACS Email resource, its sending limits, and the sending domain's DNS (SPF, DKIM, DMARC);
  - the support reply-to address (Q12);
  - the real hold lifetime (payment provider). |
| 12 | **Batch B: booking review outcomes (ADR 0025) and legal-hold release maker-checker (ADR 0026)** | **Done, without a staff tenant.**
- **Review outcomes:** `POST /api/admin/v1/orders/{orderId}/items/{itemId}/review-outcomes` (`bookings.review.resolve`). `CancelledAtSupplier` with the desk's reference (only for a booking seen not as agreed) fails the item, so nothing is charged for it. `AcceptAsBooked` works only for a booking seen not as agreed, with both confirmations, and confirms it under the seen reference at the agreed price. The payment settlement, the customer notice, the timeline and the audit entry are saved together. A repeat is `not-in-review`, and accepting without a seen booking is `no-supplier-booking-seen`.
- **Legal-hold release:** placing stays one step; `hold: false` is refused (`release-requires-approval`).
  - Privacy requests a release (one pending per order). A different person with the new `personal-data.legal-hold.approve` (new **Legal** role, and administrators) approves or rejects it; nobody approves their own (staff id or account), and requests expire after 7 days. Only the requester withdraws.
  - On approval the purge waits `LegalHoldReleaseGraceDays` (30, pending legal confirmation), and placing the hold again undoes it.
  - Endpoints: `GET .../orders/{id}/legal-hold`, `POST .../orders/{id}/legal-hold/release-requests`, `GET /legal-hold/release-requests`, `POST .../{requestId}/decision`, `POST .../{requestId}/withdrawal`. Refusals are `LegalHoldReleaseRefused` security events. Migration `AddLegalHoldReleaseRequests` (customers).
- **admin-web:** **Record an outcome** on items in review; a legal-hold panel (state, place, request, approve or reject, withdraw); **Legal-hold releases** for approvers.
- **Next:** row 13. |
| 13 | **Batch C: cancellations and refunds (ADR 0027)** | **Done, with the mock payment provider.**
- **Refund cases** (Orders, migration `AddRefundCases`, which also adds the Orders inbox and the item state `Cancelled`): `POST /api/admin/v1/orders/{orderId}/refund-cases` (`refunds.request`).
  - A **cancellation** records confirmed items as `Cancelled`, with the supplier desk's reference, and computes the refund: the supplier's refund (never more than paid for the items), less the disclosed fee (`Refunds:CancellationFee`, none by default), never more than can still be refunded.
  - A **goodwill** refund names its amount.
- **Idempotency:** an `Idempotency-Key` per request; one case per requester and key (unique index). A replay returns the case; the same key for another request is `idempotency-conflict`.
- **Maker-checker:** `POST /api/admin/v1/refund-cases/{caseId}/decision` by a different person with `refunds.approve` (new **Finance** role); only the requester withdraws.
  - **Every refund opened by staff needs a second person** (ADR 0027 §5). Expired cases stop holding money back.
  - Approval publishes `OrderRefundRequested`.
- **Refund execution** (Payments, migration `AddRefunds`, which also adds the Payments outbox):
  - One record per case id. The amount is reserved on the payment attempt in the same save (`RefundedAmount`; the attempt's rowversion serialises refunds), and released when a refund fails. Each step goes in the payment's history.
  - `payments.execute-refunds` sends it once under `{caseId}:refund` and looks unknown or pending outcomes up, never sending again. Still unknown or pending after 24 hours: `RefundUnresolved` and manual review.
  - `PaymentRefundSettled` goes to Orders (the case becomes `Refunded` or `RefundFailed`) and to Notifications ("Your refund has been sent").
- **Timeline and audit:** opening, deciding and settling a case are on the order timeline and in the audit log. `orders.watch-refund-cases` raises `RefundCaseOverdue` for an approved case not refunded within `Refunds:ExecutionTargetDays` (7).
- **Runbook:** `refunds.md`. Failure scenarios F-40, F-41 and F-43 updated.
- **Fixed later (2026-10-05):** a refund decision, and Payments' settled outcome, could lose the case's change while saving the timeline, audit and refund request, when their order load was retried (a concurrent change): the case was loaded first and detached by the retry. Both now load the order first, then the case; a regression test forces the retry. Money stayed safe (Payments refunds once per case id).
- **Next:** Batch D (row 14).
- **Pending:**
  - legal confirmation: consumer cancellation rights and refund timelines per market, and the disclosure of any fee;
  - the supplier: cancellation through its API (R8). |
| 14 | **Batch D: refund operations (ADR 0027)** | **Done, with the mock payment provider.**
- **Refund review:** `POST /api/admin/v1/payments/refunds/{refundId}/review-resolutions` (`payments.review.resolve`, Operations). A refund in manual review is settled only by a provider lookup by our key: succeeded or failed settles it (a failure releases its amount) and publishes `PaymentRefundSettled`; anything else keeps it in review, with the check in the payment's history. The resolution is tied to the refund's own correlation id. A refund of a payment without a provider id now fails at once (nothing was sent, so nothing to look up) and releases its amount, instead of a review no lookup could settle. Audited (`payments.refund-review.resolve`); it never sends a refund. The staff payment view lists the payment's refunds and the amount refunded or being refunded.
- **Customer notices:** "Your booking has been cancelled" (new `OrderCancellationRecorded`, published with a cancellation case; it names no amount, since the refund may be nothing or rejected) and "Your refund is delayed" (a failed refund, with its "Refund amount"; never told as sent).
- **admin-web:** the order page's refund panel (cases; open a cancellation or goodwill case with an `Idempotency-Key` kept across retries; approve, reject, withdraw), the approvers' "Refunds to approve" page (`refunds.approve`), and the payment page's refunds and "Check with the payment provider" for a refund in review.
- **Next:** row 15 (customer sign-in, which the customer's own cancellation request needs); supplier cancellation through its API stays blocked on R8.
- **Pending:** as row 13. |
| 15 | **Batch E: customer sign-in for customer-web (ADR 0028)** | **Done; the customer tenant is external.**
- **Decision (ADR 0028):** the customer-web token pattern left open by ADR 0008 is the same BFF as admin-web (ADR 0023), in the Api host and owned by the Customers module.
  - `CustomerSignIn` (OpenID Connect, code + PKCE, `Authentication:CustomerSession` from user-secrets or Key Vault).
  - The cookie `__Host-tb-customer`: HttpOnly, Secure, SameSite=Lax, idle 60 minutes, at most 12 hours. It holds the account only, and our customer id is mapped on every request.
  - The CSRF header `X-TB-Customer-Csrf`. A token and a session together are refused.
- **Endpoints:** `GET /api/v1/session`, `GET /session/sign-in` (503 `customer-sign-in-unavailable` until the tenant exists), `POST /session/sign-out`, and the Development-only `POST /session/development-sign-in`.
  - The `customer` policy and the anonymous-but-acting endpoints (offer selection, revalidation) accept the session; a refused session is never anonymous.
- **customer-web:** the header's Sign in / Sign out work (no visual change). Signed-in state is read in the browser only (SSR stays signed out). The interceptor adds the CSRF header to unsafe `/api/v1` calls, and a 401 ends the session.
- **Next:** the booking journey in customer-web: travellers, checkout with the mock payment, order status, then "My trips" and the customer's cancellation request.
- **Pending (external):** the customer tenant's app registration (redirect URI `/api/v1/session/callback`, client credential in Key Vault); Data Protection keys with hosting (Q2). |
| 16 | **Batch F: the customer booking journey in customer-web** | **Done, with the mock supplier and the mock payment provider.**
- **Search page:** once the price is confirmed, a signed-in customer gets "Continue to booking". It creates the order with the key `order-{selectedOfferId}`, and an order already made for that selection opens instead. A signed-out customer gets "Sign in", then selects again: an anonymous selection is never bookable.
- **`/booking/:orderId`** (client-rendered, signed in only):
  - the travellers the order needs (names as on the document, date of birth, gender) and the contact; the documents too when the airline requires them;
  - the payment, as the provider declares it (`IPaymentProvider.Entry`, `GET /api/v1/payments/entry`): test methods with the mock, unavailable otherwise, never card data;
  - the outcome: confirmed with the booking reference, being confirmed (polled), declined (try another method), or not booked (nothing charged).
- **Keys:** one `Idempotency-Key` per payment attempt, kept while the outcome is pending and new after a decline (`payment-lifecycle.md`).
- **E2E:** two booking journeys (approved; declined, then approved) and the signed-out page. CI and local E2E now apply the Customers migrations.
- **Next:** "My trips" (the customer's orders) and the customer's own cancellation request; the Stripe Payment Element once ADR 0006 is accepted.
- **Pending (external):** Stripe acceptance (ADR 0006) for real card entry and 3-D Secure challenges. |
| 17 | **Batch G: "My trips" and customers' cancellation requests (ADR 0029)** | **Done; supplier cancellation stays at the desk (R8).**
- **My trips:** `GET /api/v1/orders?limit=&cursor=` (own orders, newest first, cursor pages; new index on customer and creation time). customer-web gets a "My trips" page and nav link; each trip opens its booking page.
- **Cancellation requests** (Orders, migration `AddCancellationRequests`, additive):
  - `POST /api/v1/orders/{id}/cancellation-requests` (`Idempotency-Key`) for a booking with a confirmed item. It cancels nothing and promises nothing.
  - Unique per customer and key; at most one open per order (a filtered unique index). Withdrawable by the customer. On the order's timeline, and shown on the order (`cancellationRequest`).
- **Operations:**
  - "Cancellation requests" in admin-web (`refunds.request`).
  - Opening the cancellation case (ADR 0027) completes the request in the same save.
  - Declining is audited.
- **Customer emails:** "We have received your cancellation request" and "We could not cancel your booking" (support will contact them; the reason stays internal).
- **Runbook:** `refunds.md` ("Customers' cancellation requests").
- **Next:** the Stripe Payment Element once ADR 0006 is accepted. Until then: hotel or ancillary scope needs product decisions (Q1 for hotels).
- **Pending (legal):** a response-time target for requests; non-cancellable fares online; cancellation rights per market. |
| 18 | **Local Development identities for customer-web and admin-web** | **Done (local development and test infrastructure only; no product change).**
- **The problem:** without the Entra tenants, the apps' **Sign in** led to 503 `customer-sign-in-unavailable` and `staff-sign-in-unavailable`, so the customer and staff journeys could not be tried locally.
- **The fix:** in dev server builds only (`isDevMode()`), both apps' existing **Sign in** buttons use the Api's existing Development stand-ins (ADR 0023, ADR 0028) as fixed synthetic accounts: the test customer `0c0de000-0000-4000-8000-00000000c001` and the test staff member `0c0de000-0000-4000-8000-0000000000a1`. Production builds, and an Api that does not offer the stand-in (404), use the tenant sign-in as before. No new endpoint and no server change: the customer and staff pipelines run unchanged (mapping, policies, ownership, permissions from `Access:RoleAssignments`, CSRF).
- **Guards (tested):**
  - the stand-in is mapped only in Development with its setting;
  - startup refuses the setting in Staging and Production;
  - a Development-issued session never authenticates in Staging or Production, even when made with the host's own keys (new test, for customers and staff);
  - outside Development the routes are not mapped yet either.
- **How to:** CLAUDE.md (Dev server) and `docs/runbooks/staff-access.md`. |
| 19 | **Hardening: torn order reads** | **Done.**
- **A retried order load no longer clears the whole unit of work.** It detaches only that order's torn copy (the order, its items and timeline), so a refund case or cancellation request loaded before the order stays tracked and its change is saved. This closes the class of bug fixed in the refund handlers (row 13); loading the order first stays as defence in depth.
- **An order that keeps changing during a read** (`OrderKeptChangingException`, after five consistent-read attempts, nothing saved) is now a **503** with a generic Problem Details, through a shared `TryAgainException`, instead of a 500. Checkout already answered it itself.
- **Tests:** a case loaded before its order survives a forced retry (this test fails with the old clear-everything behaviour), and the 503 mapping. |
| 20 | **Business decisions and the card payment step (ADR 0006 accepted)** | **Done in code; live payments need external items.**
- **Decisions (2026-10-05, Claude, delegated by the project owner; revisitable, in `docs/requirements/open-questions.md`):**
  - ADR 0006 accepted (Stripe, server-confirmed);
  - Q2 markets and Q5 currencies as recorded in ADR 0006;
  - hotels (Q1) and flight + hotel packages (Q7) out of the launch scope;
  - email support at launch (Q12);
  - groups and child-only bookings not sold online (Q13).
- **Card payment step (ADR 0006, P9):**
  - The Stripe adapter declares card entry with its publishable key (`PublishableKey`, `pk_test_` only, refused otherwise), and `GET /api/v1/payments/entry` returns it.
  - customer-web loads Stripe.js from js.stripe.com, mounts the Payment Element and pays with the payment method id Stripe creates. It completes a 3-D Secure check with `handleNextAction` and repeats the request once under the same key.
  - The mock's test methods are unchanged.
- **External, still open:**
  - the merchant entity (Q14, P1);
  - Stripe's approval (P2) and test-mode keys (P3), which are needed to run the card step against Stripe;
  - the production CSP and webhook endpoint with hosting (Q2, P4);
  - live keys (P10). |
| 21 | **Hotels H1: search, offers, selection and price check (ADR 0030)** | **Done, with the mock hotel provider.**
- **Decisions (ADR 0030, delegated):**
  - merchant of record with prepaid rates only, through the same payment flow as flights;
  - pay-at-property rates not sold at launch;
  - one room per booking at launch;
  - no real hotel supplier assumed (Q6).
- **New projects:** `Modules.Hotels` (schema `hotels`, migration `InitialHotels`), `Integrations.Hotels.Mock` (Development and Staging only), and `Modules.Hotels.UnitTests`.
- **Port `IHotelProvider`** (search, revalidate) with a shared contract suite (`ProviderContracts/Hotels`), which the mock passes.
- **Offers:** the property (no supplier id exposed), room, board, the total payable now, fees payable at the property (information only), the cancellation terms (non-refundable, or free until a deadline then a penalty) and the expiry. Offers that cannot be sold or stored as stated are dropped, never truncated (F-54). Changed terms at the same price are a quote, and another property ends the selection (F-53).
- **API:** `POST /api/v1/hotels/searches` (anonymous, supplier rate limit) and `/hotels/selected-offers`, with `/revalidations` and `/price-acceptances`. These follow the same selection rules as flights: an owner or an anonymous selection, F-01..F-03, idempotent per caller, rowversion.
- **Next (H2):** hotel order items and booking through Orders (`Modules.Hotels.Contracts`, book and lookup by our reference, `PendingConfirmation` and reconciliation), then checkout. Then H3 (customer-web hotel search and booking) and H4 (cancellation by the rate's policy, vouchers, admin). |
| 22 | **Hotels H2: hotel stays ordered, paid and booked through Orders (ADR 0030 §6)** | **Done, with the mock hotel provider.**
- **`Modules.Hotels.Contracts`:** `IHotelSelections` (bookable, revalidate) and `IHotelBookings` (book once under our item id; look up by it). The architecture test `Only_orders_books_hotels` keeps the write to Orders.
- **Port:** `IHotelProvider` gains `BookAsync` and `RetrieveBookingAsync` (at most one booking per client reference). The mock books in memory, with lead-guest scenarios (rejected, timeout booked or not, price mismatch); the shared contract suite covers booking.
- **Orders:** an order item has a product (`Flight` or `Hotel`). Hotel items keep the one item state machine in the existing `FlightOrderItems` table (new `Product` column, existing rows `Flight`, migration `AddOrderItemProduct`; renaming the table and class is a later, non-destructive clean-up). Checkout revalidates, books, reconciles and reviews each item through its own product's Contracts, never another module and never a fallback. A confirmed hotel item has its confirmation number as the booking reference and no ticketing.
- **API:** `POST /api/v1/orders` takes an optional `product` (`Flight` by default, or `Hotel`); order items show their `product`. Additive only.
- **Decisions (delegated):**
  - every guest is named before payment, with no travel documents;
  - children's ages are their ages **at check-out** (as large OTAs ask), the date traveller details are checked against, so a birthday during the stay never blocks payment;
  - guests are counted for traveller details by the Customers age rule (under 2 an infant, 2 to 11 a child, otherwise an adult); the supplier gets the real occupancy instead: adults (18 or over) and each child's age, from their date of birth, which must match the ages the room was priced for or nothing is sent;
  - the lead guest (an adult) is sent first;
  - a supplier answer "booked" with no reference is unknown (looked up again), not a mismatch; the property's own confirmation code arriving later stays F-51;
  - booking emails use product-neutral wording;
  - guest data is kept until check-out, by the existing retention rule.
- **Follow-ups (recorded):**
  - rename the C# types `FlightOrderItem`, `FlightOrderItemStatus`, `FlightBookingOrchestrator` and `CreateFlightOrder*` to product-neutral names, with no migration (the table name stays). The public schema `CreateFlightOrderRequest` keeps its name in v1, deliberately;
  - before hotel cancellation (H4), read the booked rate's cancellation terms (deadline and penalty) from Hotels for the refund decision;
  - check the guests' ages against the searched ages before payment, once customer-web collects them (H3); today a mismatch is refused before anything is sent, and the hold is released.
- **Worker** composes Hotels (and its mock in Development and Staging) to reconcile hotel bookings. Hotels' database is needed only when it is used, so flights never depend on its configuration; E2E and CI apply the Hotels migrations too.
- **Next:** H3 (customer-web hotel search, selection and checkout, functional only), then H4 (cancellation by the rate's policy, vouchers, admin). |

**Preconditions for any payment endpoint** (security review, chunk 2):
- Bind the payment-method token as a string in the public `*Request` and build `PaymentMethodToken` in the handler, so the result is a 400, not a 500.
- Validation problems never echo the value, and request/body logging excludes the route.
- A permission-matrix test covers cross-customer authorize, capture, void and refund.
- `*Details` records are never logged or serialized.
- In Production, `PaymentOperations` must fail at startup without a provider.

**Preconditions for any Orders or checkout endpoint:** Q8 is answered and the order owner, per-customer keys and hidden foreign order ids are done (chunk 3). Still needed:
- ADR 0008 accepted with a configured identity provider (the token validation and the internal customer id are done, row 4c; the customer id is taken from the token only).
- Selection ownership, replay semantics, the attempt cap with its security event, generic declines and the operator way out of ManualReview payment attempts are done (row 4d2). Still needed: the staff identity and an admin endpoint for that resolution (the operations batch), alerting on the conditions in `docs/runbooks/payment-hold-release.md`, a per-customer request rate limit on customer endpoints, and the limits' values decided (Q10).
- The HTTP permission matrix, including cross-customer attempts.
- Create `Modules.Orders.Contracts` with its first consumer.

**Amadeus next (gated):** R1 (Amadeus product) and R2 (test credentials) unblock the sandbox contract run and SandboxVerified; R5–R7 then allow the Amadeus booking ADR and booking and lookup; R3, R4 and R8–R13 are needed for ProductionReady (ADR 0019).

**Follow-ups from the ADR 0006 groundwork (reviews):** after PR #30 merges, rewrite `SupplierHttp.SendAsync` as `SendRawAsync` plus the status mapping (one copy of the transport handling); back-off for failing notifications (today every 10 seconds, up to 10 attempts; the attempt itself stays on the reconciliation list); a dedicated rate-limit policy for the notification route before the public webhook endpoint (P4); an Authorized hold that lapses (F-24) is detected with capture orchestration, not by notifications.

**Follow-ups from the Q6 preparation:** the resilience package at the marked hook (ADR 0003; ask first); the provider id on Orders items with booking orchestration; an optional cabin-bag allowance in the port; market and charge-currency context on search (ADR 0017); per supplier, the steps in the onboarding runbook.

**Follow-ups from the row 4 reviews:**
- An Authorized hold without a release request is never looked up, so one that lapses at the provider stays live and blocks new attempts. Look it up once it passes the provider's authorization lifetime, which comes with the payment provider's ADR (0006).
- A lookup the provider keeps refusing during a void is retried every run. Count failed lookups, and move the attempt to ManualReview after a limit.
- Outbox event types are stored by CLR name: give events a stable declared name before any is renamed.
- In Staging, the mock payment provider's per-process state means Worker lookups cannot see payments made through the Api. Share it, or disable the Payments jobs there, before Staging is used for payment testing.

**Follow-ups from the chunk 3 reviews:** a later Orders migration can drop the `CustomerId` default and add `CHECK (CustomerId <> '')` once no dev rows lack an owner. The ARCHITECTURE REVIEW on synchronous cross-module commands is **resolved by ADR 0015 (Accepted 2026-09-26)**. Checkout's `IFlightSelections.RevalidateAsync` and `IOrderPayments.AuthorizeAsync`/`ResumeAsync` are allowed as idempotent, supplier-neutral commands with explicit unknown states. Durable side effects and background work stay on the outbox and Worker, and any other synchronous command needs its own ADR.

### Phase 3: flights model readiness (audit batch 2)
**Done (this PR):**
- The supplier-neutral fare and leg model: price breakdown, validating and operating carrier, baggage, refund/change conditions, ticketing deadline, flying time and fare basis.
- Provider resolution by `ProviderId` for revalidation, booking and lookup.
- An airport seed dataset with IANA zones (`IAirportDirectory`).
- The API search response now carries fare facts, airports and flying time; the selected-offer response carries fare facts. The client is regenerated.
- Selected offers keep the fare facts (migration `AddSelectedOfferFare`, itinerary snapshot v2; v1 rows stay readable). The results page shows the nonstop flying time, codeshare operators and a fare summary, with no redesign.

**Deferred:**
- multi-provider search (fan-out);
- an authoritative airport source (before production);
- fare-rule penalty amounts;
- storing the provider id on the Orders item, which supplier-booking reconciliation needs (`FlightSupplierBooking.ReconcileAsync(providerId, ...)`), with the booking orchestration.

## Phase 2 — Flights slice (complete)

Phase 1 is **complete**. Q3 was answered on 2026-09-25: **flights first**. The direction is: flight search UI → API → Flights module → `IFlightProvider` → deterministic mock → results. No real supplier (Q6), payment design (Q1), or market or hosting decision (Q2) is assumed.

### Phase 2: plan
| # | Chunk | Status / depends on |
|---|---|---|
| 1 | Flight **search port** (`IFlightProvider.SearchAsync`, with its types in `Modules.Flights.Ports`). `BuildingBlocks`: `Money`, `CurrencyCode`, `Result`, and the provider error taxonomy. Deterministic **mock provider** (`Integrations.Flights.Mock`: XTS test currency, carrier ZZ, scenarios selected by configuration). **Provider contract suite** (`tests/backend/ProviderContracts`). Architecture rules for ports and adapters | **Done** (ADR 0014 Accepted). The mock refuses Production and undefined scenarios at startup. `ProviderOfferRef.Value` is an opaque adapter token |
| 2 | Flight search **API endpoint**: validation, mapping provider errors to ProblemDetails, contract snapshot and client. The host registers the mock outside Production. Delete `Modules.Sample` (oasdiff ignore file, ADR 0013). Development-only until rate limiting exists | **Done.** `POST /api/v1/flights/searches` (anonymous, Development-only). Provider errors map to 503 `provider-unavailable`, 422 `search-rejected`, or 502 `provider-error`. No offer id is exposed until offer selection. Dates are bounded between yesterday (UTC) and a 361-day sales horizon (a supplier constraint to revisit with Q6). Cabin accepts documented names only. The mock runs only in Development or Staging. `Modules.Sample` is deleted; its removal is accepted in `src/backend/Hosts/Api/openapi-accepted-breaking-changes.txt` |
| 3 | customer-web **search UI and results** through the generated client, with a Playwright journey test and axe checks | **Done.** The search page is the home route: form, validation, loading, empty and error states, results, and client-side offer selection. It calls the generated client on the same origin (dev proxy; the deployed gateway is part of the hosting story). Server routes are per route (search prerendered, all other routes client-rendered). Playwright covers the journey against the real Api and mock, plus empty, outage, invalid-input, and phone-width cases, all with axe |
| 4a | **Offer selection backend** (Option 2). Search results are held in HybridCache under a random `searchId` with per-offer ids (TTL no later than the earliest offer expiry; keys carry no PII). `POST /api/v1/flights/selected-offers` persists **only** the selected offer's supplier-neutral snapshot in SQL Server (EF Core, schema `flights`, migration `InitialFlights`). Selection is idempotent (unique `(SearchId, OfferId)`: 201, then 200) and F-02 (expired or evicted search, unknown offer, expired offer) returns 422 `offer-expired`. Testcontainers integration tests; OpenAPI and client updated | **Done** |
| 4b | customer-web **selection flow** calling `selected-offers` (with expired-offer handling), the **Aspire AppHost** (Api + SQL Server) for local runs, and E2E with a real database | **Done**, except the AppHost. Selection goes through the API by server offer id, with one save at a time. The saved selection shows route, total, and "held until". A 422 `offer-expired` shows "Offer no longer available" with **Search again**. E2E runs against a real SQL Server: a local container, or one CI provisions per run with the migrations applied. The Api uses its own E2E port (5099). **AppHost deferred:** on the current dev machine Docker runs only inside WSL, so an AppHost on Windows cannot start SQL Server; add it once a Windows-reachable container runtime exists (ADR 0003 unchanged) |
| 4c | customer-web **flight UX redesign** (UI only; no API or backend change) | **Done**. A small design system in `styles.css` (tokens; buttons, tiles, cards, badges, chips, segmented controls, alerts, skeletons, dialog sheets), with an orange/black/white palette and a neutral "Travel booking" placeholder brand (no product name decided). The header has Flights active and Hotels, My trips and Sign in shown as "Soon" placeholders, plus a mobile menu. The search module has one way / round trip (multi-city shown as "Soon"), code-only airport inputs with swap (the API has no airport names), an accessible two-month range calendar, and a traveller/cabin stepper. Loading skeletons and polished error, empty and outage states (with **Try again**). The results marketplace has a search summary, sort (price, departure, arrival) and filters (stops, departure time, airline code) derived only from the returned offers, with a mobile filter sheet. The cards show local times, stops, via airports, and +1-day arrivals; there is no duration, airline name or baggage, because the API does not return them. Selection appears as a sticky summary. Checked at 360, 390 and 768 px and on desktop |
| 5 | **Revalidation** (`RevalidateAsync`): F-01 price changed, F-02 offer expired, F-03 sold out | **Done.** `IFlightProvider.RevalidateAsync(ProviderOfferRef)` is an idempotent read that returns the currently priced offer. The mock reprices statelessly from its token; the destinations `ZPC`, `ZEX` and `ZSO` select F-01, F-02 and F-03. `SelectedOffer` has a state machine: `Selected` → `Confirmed`, or `PriceChanged` (a quote) → `Confirmed` once the quote is accepted, or `Expired` / `SoldOut` (terminal). The selected price is kept, a changed price (higher or lower) is only a quote until the customer accepts that exact quote, and the table has a rowversion and a status check constraint. Migration `AddSelectedOfferRevalidation` is additive (existing rows become `Selected`). API: `POST /flights/selected-offers/{id}/revalidations` returns 200 when confirmed, or 422 `price-changed` (previous and new totals, `priceQuoteId`, `requiresConfirmation`), `offer-expired` or `sold-out`. `POST .../price-acceptances` takes `{priceQuoteId}` and returns 200 (replay 200) or 409 `price-quote-stale`. Other errors: 404 unknown selection, 409 concurrency conflict, 503 supplier unavailable. customer-web adds **Confirm price** and a price-change prompt (**Accept new price** / **Choose another flight**) to the selection bar, plus a sold-out state; there is no visual redesign. The same-origin anonymous endpoints are on the exposure checklist below |
| 6 | **Supplier booking operations** without orders or payment | **Done.** The port gains `BookAsync(FlightBookingDetails)`, with our `ClientReference` as the supplier idempotency token and the agreed price as `ExpectedTotalPrice` (a different price is not booked), and `RetrieveBookingAsync(ClientReference)`. Passenger names are never printed. The mock scenarios are success, rejected, timeout-booked and timeout-not-booked, chosen by family name; sold out and expired also apply at booking. The provider contract suite covers booking and lookup: found again, no second booking for the same reference, a wrong price is not booked, a passenger mismatch is invalid, an unknown reference is a definite not-found, and cancellation. The core's `FlightSupplierBooking` makes exactly one supplier write and classifies it as `Booked`, `NotBooked(reason)` or **`Unknown`**; a timeout is never a failure and is never resubmitted. `ReconcileAsync` looks the booking up by our reference and returns `Booked`, `NotBooked(NotFound)` or still `Unknown`. **Nothing is persisted and there is no endpoint**: a supplier booking belongs to an Order item, and ADR 0005 rejects both a standalone FlightBooking and booking before payment, so the OpenAPI contract is unchanged. Api-host integration tests book persisted, revalidated selections through each scenario. After the booking-flow and architecture reviews: an exception from a started write is `Unknown`; Unavailable, RateLimited and AuthFailure on a write are `Unknown`; a lookup that finds nothing is `NotFound` as of an instant, not a definitive failure; and a booking not as agreed (price, reference or provider) is a `Mismatch` for manual review. Adapters must guarantee at most one booking per client reference, and the contract covers parallel requests and a repeat with other details |
| 7 | **Exposure groundwork (platform-neutral)**: per-client rate limiting and trusted forwarded headers | **Done, up to the hosting gate.** The built-in ASP.NET Core rate limiter (no new package) is partitioned by the connection's client address (IPv4 per address; IPv6 per /64 prefix, so rotating addresses within one allocation does not escape the limit) and never reads a forwarding header itself. The `/api/v1` group defaults to the `anonymous` policy (60/min per client); search and revalidation, which call the supplier, use `supplier-calls` (20/min). Rejections are 429 Problem Details `rate-limited` with `Retry-After`, documented on all four flight endpoints (additive OpenAPI change). Forwarded headers: `ForwardedHeaders:KnownProxies/KnownNetworks` is **empty by default, which switches forwarding off** (ASP.NET trusts every sender when both lists are empty, so they are never just cleared). Only the nearest hop is used (`ForwardLimit = 1`); the middleware runs first; configuration is validated at startup. Tests cover the endpoint metadata, per-client buckets, spoofed headers without a trusted proxy, a configured proxy or network, the nearest hop only, health not limited, and startup validation. **Flight endpoints stay Development-only**; the remaining gate is below |
| 8 | **Next (proposed)**: none technically unblocked in the Flights plan. Phase 3 (Orders and payments) needs approval and Q1. The exposure gate needs the hosting decision and Q2 | Proposed |

**Follow-ups from the chunk 2 reviews:**
- **Decided 2026-09-25 (Option 2): offers are held temporarily in server-side HybridCache (ADR 0011), identified by an opaque `searchId`, and only the selected offer is persisted.** Implications for chunk 4:
  - `POST /flights/searches` returns a `searchId` and per-offer ids (additive), together with the cache that makes them resolvable.
  - The cached offer set expires no later than the offers' `expiresAt`.
  - Selection loads the offer from the cache and persists its snapshot in SQL. An expired or evicted search means re-searching (F-02).
  - The cache is never the booking source of truth, and the selected offer is revalidated with the supplier before booking (chunk 5).
  - This builds on ADR 0011, which was accepted on 2026-09-25.
  - Until then, customer-web selects an offer client-side by its position in the result set.
- ~~Remove the `Modules.Sample` entry from `openapi-accepted-breaking-changes.txt`~~: done in chunk 4a.
- **Exposure checklist: before flight search or offer selection is mapped outside Development** (security and architecture reviews of chunks 2 and 4a):
  - ~~Forwarded headers and per-client rate limiting on the four anonymous flight endpoints, with the endpoint-metadata test~~: groundwork done (chunk 7). **Still gated on the hosting decision (and Q2):** each environment's trusted ingress addresses (`ForwardedHeaders:KnownProxies/KnownNetworks`); `AllowedHosts`; a distributed limiter or Redis once there is more than one Api instance (limits are per instance, ADR 0011); tuning the per-client limits; setting `ForwardLimit` to the number of trusted proxy hops if the topology chains them (it is 1 today, correct for a single ingress; more hops would put every client in one bucket); and a test that Production sends HSTS behind the ingress. Only then may `MapFlightsEndpoints` (and a provider registration) widen beyond Development.
  - Mock bookings live in memory: bound them before a booking endpoint exists in Staging (security review, chunk 6).
  - Selections are addressed by an unguessable server-issued id and are anonymous until identity (Phase 4). Once customers sign in, the revalidation and acceptance endpoints need an ownership check (no IDOR). An endpoint test now lists the anonymous `/api` routes explicitly, so a new one fails until it is added deliberately.

**Phase 3 acceptance criteria carried from the chunk 6 reviews:**
- Orders never hold the provider token or supply a price. Flights exposes a Contracts entry point shaped like `BookSelectedOffer(selectedOfferId, clientReference = OrderItemId, passengers)` and `Reconcile(clientReference)`, with its own DTOs and outcome type. Behind it, Flights loads the `Confirmed`, unexpired selection and uses its stored token and agreed price. `FlightSupplierBooking` stays internal.
- The orchestrator voids only on `NotBooked`, or on `NotFound` after the supplier's consistency window has passed. `Unknown` stays `PendingConfirmation` (Worker reconciliation, then ManualReview), and `Mismatch` goes to ManualReview. Nothing is ever resubmitted.
- Passengers are persisted only in the traveller or order-item store. `FlightBookingDetails` never goes in the outbox, cache or logs, and supplier captures go through the redaction layer. The booking/traveller `*Request` validates the name format and the date-of-birth range. Documents are Sensitive PII (encrypted, audited).

**Follow-ups from the chunk 5 reviews (for chunk 6 / Phase 3):**
- Booking may only use a `Confirmed` selection from a fresh revalidation. `IsAvailable` means "can still be revalidated", not "ready to book".
- Re-selecting an existing snapshot (`POST /selected-offers`) returns 200 with its original price and expiry whatever its status. Consider exposing `status` and the agreed price, or answering 422 for terminal selections.
- An accepted-quote replay after the offer's expiry still returns 200. Booking must revalidate first anyway.
- Check that the revalidated itinerary is unchanged (a schedule change at pricing time); see provider-integration.md.
  - A size limit on the in-memory cache that bounds memory for anonymous searches. Verify first that HybridCache sets entry sizes, or the underlying memory cache rejects unsized entries.
  - A retention job that deletes expired selected offers that never became orders.
  - When identity arrives (Phase 4), bind a selection to the customer or session. Today `searchId` + `offerId` is a bearer capability, which is acceptable only because the snapshot holds no PII.
  - Known and accepted: a duplicate-click race makes EF Core log the duplicate-key error (including the ids) before it is handled; an ambiguous commit retried by the execution strategy returns 200 instead of 201 for its own row.
  - Add a `Location` header once `GET /selected-offers/{id}` exists.
- Money amounts are passed through at the adapter's scale; rounding to ISO minor units (ADR 0010) arrives with pricing, so the UI must not assume a fixed number of decimals.
- **Hosting-story prerequisite (from the chunk 3 review):** the first customer-web route that fetches data during SSR needs an absolute API URL on the server, via a server-only `provideApiConfiguration(...)` in `app.config.server.ts`, fed from server config. The SSR server does not handle `/api` itself, so the deployed gateway path stays untested until the hosting story.
- When the i18n ADR lands (Q2), mark the search page for extraction, including the status plural (currently string concatenation) and the cabin labels.
- CODEOWNERS for the contract files was suggested; ADR 0012 defers CODEOWNERS until there is a second technical owner.

**Gates:**
- Booking and orders with payment need **Q1**.
- A real supplier needs **Q6**. That supplier also validates the port against a second supplier shape before the port is frozen (ADR 0004).
- Exposing search in production needs the hosting decision and **Q2**. The forwarded-headers and rate-limiting mechanisms exist (chunk 7); the trusted ingress configuration does not.
- ADR 0004 and ADR 0014 were accepted on 2026-09-25.

## Phase 1 — Skeleton (complete)

Phase 0 is **complete** (all exit criteria below are met). Phase 1 was approved on 2026-09-25. There is still **no business code**: do not implement flights, hotels, bookings, payments, databases/migrations, or authentication until the story says so.

### Phase 1: done
- Application skeleton (branch `feat/phase-1-application-skeleton`):
  - `global.json` (SDK 10.0.401, Microsoft.Testing.Platform for `dotnet test`), `Directory.Build.props` (nullable, warnings as errors), `Directory.Packages.props` (central package management), `TravelBooking.slnx`.
  - Hosts: `src/backend/Hosts/Api` (minimal APIs, ProblemDetails, health endpoint) and `src/backend/Hosts/Worker` (empty host, ready for ADR 0007 background processing).
  - `src/backend/Modules/Sample/Modules.Sample`: **spike module, Development only.** It proves the module pattern (`AddSampleModule` / `MapSampleEndpoints`, internal handlers) and .NET 10 built-in validation from a module library. **Deleted in Phase 2** with the flight search endpoint, as planned when the first real module was created, together with `tests/backend/Modules.Sample.UnitTests` and `tests/backend/Api.IntegrationTests/SampleValidationTests.cs`.
  - Tests: `tests/backend/Modules.Sample.UnitTests`, `tests/backend/Api.IntegrationTests` (WebApplicationFactory; includes the every-endpoint-declares-authorization check), `tests/backend/ArchitectureTests` (ArchUnitNET module boundary rules).
  - `src/frontend`: Angular 22 CLI workspace with the `customer-web` shell (SSR/prerender, zoneless, Vitest) and, since then, the `admin-web` shell (item 3). No pages beyond the shells.
- ADR 0003 spike results: built-in validation works from module libraries when (1) each module calls `AddValidation()` itself and (2) validated request types are public; `[ValidatableType]` is experimental (ASP0029) and is not used. ArchUnitNET runs on xUnit v3 via `TngTech.ArchUnitNET.xUnitV3`. Recorded in `.claude/rules/backend-dotnet.md`.

### Phase 1: next
1. CI workflow: **done** (`ci.yml`: Backend, Frontend, API contract, Secret scan; `codeql.yml`: C# and JavaScript/TypeScript, plus weekly). Next: make its checks required on `main` once they have passed on GitHub (ADR 0012). **Dependabot version updates: done** (`.github/dependabot.yml`: weekly NuGet, npm, and Actions updates; Angular and ASP.NET Core packages grouped; framework, TypeScript, and `@types/node` majors excluded as deliberate upgrades).
2. OpenAPI: **document done.** `/openapi/v1.json` is served in Development only. The committed contract snapshot `src/backend/Hosts/Api/openapi.v1.json` is enforced by `OpenApiContractTests`, which also check that validation constraints and 400 ProblemDetails are documented. JSON numbers are strict, and malformed requests return 400 ProblemDetails in every environment. **Generated client done** (ADR 0013, Accepted): `ng-openapi-gen` produces `src/frontend/projects/api-client` (`@travel-booking/api-client`), committed. CI fails if the client is stale, type-checks it, and runs oasdiff against the base branch's contract (the `API contract` job, PRs only). Scalar UI is deferred until someone needs it. The `Modules.Sample` removal was accepted through the committed oasdiff ignore file (`src/backend/Hosts/Api/openapi-accepted-breaking-changes.txt`).
3. `admin-web` SPA shell (ADR 0009): **done.** Zoneless, OnPush, and `noindex`. Critical-CSS inlining is off so the build has no inline scripts or event handlers, which `tools/check-strict-csp.mjs` enforces in CI (strict `script-src`). The CSP header itself comes with the hosting and security-headers story. `tools/check-app-boundaries.mjs` enforces in CI that apps never import each other. Staff SSO arrives in Phase 4.
4. Aspire AppHost and Docker/Podman for integration-test infrastructure (ADR 0003), when the first database-backed story needs them.
5. BuildingBlocks, only when a second module or a real need requires shared code.
   - E2E and accessibility harness: **done.** `tests/e2e` is its own npm package (Playwright 1.63, `@axe-core/playwright`), running against the production builds: the real customer-web SSR server, and admin-web served under a strict CSP. Shell smoke tests (landmarks, keyboard skip link, no console errors), a runtime strict-CSP check for admin-web, and axe WCAG 2.2 AA scans of both home pages. The CI `E2E` job caches Chromium by Playwright version. Booking-journey E2E tests go in `tests/e2e/specs/<app>/` when the journeys exist.
6. Known follow-ups from the skeleton reviews:
   - **Fallback authorization policy is deferred to Phase 4** (ADR 0003 names it). Without an authentication scheme it turns unmatched routes into 500s. Until then, deny-by-default rests on `EndpointAuthorizationTests`, which checks Development, Staging, and Production.
   - When the second module arrives, add an API test proving validation works for endpoints in **each** module (each module calls `AddValidation()`).
   - When the first real `customer-web` route is added, replace the catch-all prerender with per-route render modes and `RenderMode.Client` as the catch-all (ADR 0009: booking and authenticated flows are client-rendered).
   - Provider port visibility (the ARCHITECTURE REVIEW raised in Phase 1): **addressed by ADR 0014** (Accepted). Ports live in a public `Modules.<Area>.Ports` namespace, enforced by the architecture tests.
7. customer-web SSR server: **final error handler done.** `server-error-handler.ts` returns a generic 500 and never a stack trace; it logs the path without the query string. Unit-tested and smoke-tested on the production build.
   API transport hardening: **done.** Security headers on every application response, including errors, plus HSTS outside Development (`nosniff`, `DENY`, CSP `default-src 'none'; frame-ancestors 'none'`, `no-referrer`); no `Server` header; `AllowedHosts` fails closed to `localhost`, so **each deployment must set `AllowedHosts`**; `Cors:AllowedOrigins` is an explicit allow-list: empty by default; canonical origins only; `http://localhost` in Development only; no wildcards, user info, or credentials; validated at startup. Tested in `SecurityHardeningTests`.
   Remaining hosting and security-headers story: **rate limiting waits for forwarded headers**, because partitioning by client IP behind an ingress would otherwise put every user in one bucket. Both **must be in place before the first search, booking, or auth-adjacent endpoint is exposed outside Development**; `UseForwardedHeaders` restricted to the platform ingress before HSTS/HTTPS redirection, with a test that Production sends `Strict-Transport-Security`; `NODE_ENV=production` in the customer-web server image (its final error handler already never exposes error details, whatever `NODE_ENV` is); customer-web's SSR output contains Angular's inline event-dispatch and hydration scripts, so its CSP needs nonces or hashes; Angular component styles need `style-src 'unsafe-inline'` or a host-injected nonce (`ngCspNonce`) in both apps, and that story decides which; Angular SSR `allowedHosts` set to real hostnames; the frontend apps' CSP headers. The `/health` endpoint stays status-only when detailed checks are added.

### Phase 0: done
- Architecture review and foundation proposal approved.
- Claude Code environment: `CLAUDE.md`, `.claude/rules/` (9), `.claude/skills/` (3), `.claude/agents/` (3), `.claude/settings.json`, and the secret-guard hook.
- Documentation structure: requirements, architecture drafts, ADRs 0001–0012, quality docs, runbook template.
- Repository hygiene: `.gitignore`, `.gitattributes`, `.editorconfig`, PR template.
- Initial commit `dbeed7b` (`chore: establish engineering foundation`) pushed to `origin/main` on GitHub.
- GitHub repository security settings configured: branch protection on `main`, secret scanning + push protection, Dependabot alerts (confirmed by the developer on 2026-09-25; not independently verified from tooling).
- Q4 answered (one developer; the client/business owner reviews releases). Recorded in [`requirements/open-questions.md`](requirements/open-questions.md), with the review model in ADR 0012.
- Phase 0 exit criteria and phase-entry gates documented (below). ADRs 0001, 0010, and 0012 revised.
- Phase 1 ADR decision (2026-09-25): **Accepted** 0001, 0002, 0007, 0009, 0010, 0012. **ADR 0003 stays Proposed** until the Phase 1 plan deliberately decides its four open choices: minimal APIs vs controllers, validation library, assertion library, and architecture-test library. ADRs 0004, 0005, 0006, 0008, and 0011 remain Proposed. ADR 0003 was then accepted with those four choices (PR #2).
- Reviewer-agent corrections: reviewers now receive a brief (story, acceptance criteria, changed files, diff); the Definition of Done requires `architecture-reviewer` for structural/boundary changes; the architecture reviewer checks the frontend → API boundary; the security reviewer checks error exposure and permission-matrix tests; the ADR exemption for the three initial reviewers is documented; and the failure-scenario ID scheme and count are documented in the catalog.

### Phase 0 exit criteria
Phase 0 closes when **all** of these are true. Q1, Q2, and Q3 do **not** block Phase 0; they are phase-entry gates (below).

| # | Criterion | Status |
|---|---|---|
| 1 | Engineering foundation complete (docs, rules, quality docs, repository hygiene) | Done |
| 2 | GitHub repository established (initial commit pushed) | Done |
| 3 | Repository security/protection configured (branch protection on `main`, secret scanning + push protection, Dependabot alerts) | Done (developer-confirmed) |
| 4 | Claude Code engineering environment established (`CLAUDE.md`, rules, skills, reviewer agents, secret-guard hook) | Done |
| 5 | Q4 answered | Done |
| 6 | Phase 1 architectural ADRs accepted or revised: 0001, 0002, 0003, 0007, 0009, 0010, 0012 | Done. Accepted: 0001, 0002, 0007, 0009, 0010, 0012. **0003 deliberately kept Proposed** (its four choices are decided in the Phase 1 plan) |
| 7 | Explicit Phase 0 exit criteria documented | Done (this section) |
| 8 | This documentation/ADR update reviewed and committed through the PR workflow | Done (PR #1, merged as `587a99b`; ADR 0003 accepted in PR #2, `499799e`) |

Business answers for Q1, Q3, Q6, Q2, and Q5 should start early: they gate later phases (see below).

## Phase-entry gates
Business questions that must be answered before specific later work. A gate is **not** an answer: each question stays Open in [`requirements/open-questions.md`](requirements/open-questions.md) until the business decides it.

| Question | Must be answered before | Architecture it unblocks |
|---|---|---|
| Q1 Merchant of record | **Answered for flights 2026-09-25 (merchant of record).** Still a gate for hotels | ADR 0005 (accepted for flights), ADR 0006 (Proposed: its provider choice also needs Q2 and Q5) |
| Q3 First product | Implementation priority is finalised; the Phase 2 product slice (which provider port and mock come first) | ADR 0004 (first port), Phase 3 slice |
| Q6 Target suppliers | The supplier provider ports are frozen; Phase 5 | ADR 0004, per-supplier ADRs |
| Q2 Launch markets | Production-market, compliance, and hosting-region decisions; production-oriented design in Phase 3 | Compliance scope, data residency, i18n ADR (per ADR 0009) |
| Q5 Charge currencies and FX policy | Real payment/currency integration (Phase 5) | Separate FX-policy ADR (ADR 0010 leaves it out of scope) |

Q7–Q12 remain tied to the later phases listed in [`requirements/open-questions.md`](requirements/open-questions.md).

## Deferred prerequisites
- **Docker Desktop or Podman** (for Testcontainers and the Aspire AppHost): required when integration-test infrastructure is introduced (Phase 1, next items). Not installed yet.
- **GitHub CLI (`gh`)**: optional convenience for PR work; not required.
- **`TBD` values** in [`requirements/non-functional.md`](requirements/non-functional.md) (availability, RPO/RTO, performance, retention): needed before the first production deployment, not to close Phase 0.

## Roadmap (each phase needs explicit approval)

| Phase | Scope |
|---|---|
| 1 — Skeleton | `global.json`, backend solution (Api, Worker, Aspire AppHost, BuildingBlocks, architecture tests), CI workflow (build, test, format, OpenAPI diff, secret scan, CodeQL), Angular workspace skeleton (`customer-web`, `admin-web`), fill CLAUDE.md commands |
| 2 — Provider ports | `IFlightProvider` / `IHotelProvider` / `IPaymentProvider`, scenario-driven mock providers, provider contract suites; `add-provider-adapter` skill |
| 3 — First vertical slice | One product end-to-end with mocks: search → offer → revalidate → authorize → book → capture → confirm, with failure scenarios; `ef-migration` skill |
| 4 — Identity & admin foundation | External IdP integration, permissions, audit log, admin shell, booking timeline view |
| 5 — Real integrations | Stripe (test mode), first real supplier sandbox, reconciliation jobs |
| Later | Second product, refunds/cancellations UI, notifications, reporting, B2B |

## Open blockers
- **Phase 0:** none (complete).
- **Phase 1:** `main` currently merges with merge commits; ADR 0012 expects squash merges and linear history. Enable "require linear history" and use squash merge.
- **Later phases:** the phase-entry gates above (Q1, Q3, Q6, Q2, Q5).
