# Failure-scenario catalog

The canonical list of failure scenarios the platform must handle. Each row names the expected behaviour and the **minimum** test level. When a story implements or touches a scenario, add the test and update **Status**.

Status: `Planned` → `Covered (link to test)` → `Verified E2E`.

Levels: U = domain/application unit · I = integration (real DB) · A = API · C = concurrency · P = provider contract · E = Playwright E2E.

**Identifiers and count.** This file is the only authoritative source for scenario IDs and the scenario count. It currently holds **42 scenarios in 7 categories**. IDs are numbered in bands of ten per category, so gaps are intentional, and the highest ID (F-62) is not the count:

| Band | Category | Scenarios |
|---|---|---|
| `F-0x` | Pricing and offers | F-01–F-04 (4) |
| `F-1x` | Supplier booking | F-10–F-17 (8) |
| `F-2x` | Payments | F-20–F-26 (7) |
| `F-3x` | Duplicates, ordering, and concurrency | F-30–F-38 (9) |
| `F-4x` | Cancellation and refunds | F-40–F-44 (5) |
| `F-5x` | Supplier-initiated changes | F-50–F-52 (3) |
| `F-6x` | Security and personal data | F-60–F-66 (7) |

Add a new scenario with the next free ID in its category's band, and update this table in the same change. Never renumber or reuse an ID, because tests and PRs reference them. Other documents refer to scenarios by ID and must not restate the count.

## Pricing and offers
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-01 | Price changed at revalidation | 422 `price-changed` with new breakdown; no authorization; customer must accept | U, A, E | Done for the selected offer (chunk 5): 422 `price-changed` with the previous and new totals and a `priceQuoteId`. It is accepted only by that id; a stale quote gets 409. Price breakdown and the authorization step come with pricing and payments Checkout step done (Phase 3 chunk 3): right before payment every order item is revalidated with the supplier, a changed price stops the checkout (no authorization), and an order adopts a new price only with a newly accepted quote (U, I). **Order endpoint (row 4c):** an unchecked price, or a change not accepted, is 409 `price-check-required`; an accepted change is ordered with its consent evidence (A: CustomerOrderEndpointTests). |
| F-02 | Offer expired before checkout | 422 `offer-expired`; item `Expired`; prompt re-search | U, A, E | Done for selection and revalidation (chunks 4–5). The selected offer becomes `Expired` (terminal) and the customer is asked to search again. The order-item state comes with orders Checkout step done (Phase 3 chunk 3): an expired offer, or one with under two minutes left, is never paid for (U). Background expiry done (row 4): an unpaid order whose offer expired is abandoned only once no payment attempt may hold funds; an unused hold is released first (U, I). **Order endpoint (row 4c):** an expired or unknown selection is 422 `offer-expired`, and a sold-out one 422 `sold-out` (A). |
| F-03 | Sold out at revalidation or booking | Revalidation: 422 `sold-out`. Booking: `Failed`, authorization voided | U, P, E | Revalidation done (chunk 5: 422 `sold-out`, selection `SoldOut`, search again; U, P, A, E). Booking: the supplier outcome `NotBooked(SoldOut)` is done (chunk 6); `Failed` and the void come with Phase 3 Checkout step done (Phase 3 chunk 3): a sold-out offer is never paid for (U). |
| F-04 | Client submits a tampered price | Ignored; server price used; audit/security event | A | Planned |

## Supplier booking
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-10 | Provider timeout on book | `PendingConfirmation`; 202 to client; **no resubmit**; reconciliation resolves | U, P, E | Supplier level done (chunk 6): a timeout is `Unknown` and is never resubmitted (U, P, I). `PendingConfirmation`, the 202 response and the Worker come with Phase 3 orders |
| F-11 | Timeout, booking actually created | Reconciliation finds it → `Confirmed` → capture | U, P, I | Supplier level done (chunk 6): the lookup by our reference finds the booking (U, P, I). `Confirmed` → capture come with Phase 3 |
| F-12 | Timeout, booking not created | Reconciliation confirms absence → `Failed` → void | U, P, I | Supplier level done (chunk 6): the lookup finds nothing, giving `NotFound` as of an instant (U, P, I). Not conclusive until the supplier's consistency window has passed; then `Failed` → void (Phase 3) |
| F-13 | Pending unresolved past limit | `ManualReview`; alert; ops queue item | U, I | Planned |
| F-14 | Provider unavailable / circuit open | No booking attempt; clear message; search degrades per provider | U, P, E | Supplier level done (chunk 6): Unavailable, RateLimited and AuthFailure on a write are `Unknown` (a gateway error may follow a processed request), settled by one lookup (U, P, I). The customer message comes with Phase 3 |
| F-15 | Definitive supplier rejection | `Failed`; authorization voided; customer informed | U, P, E | Supplier level done (chunk 6): the booking is `NotBooked(Rejected)` (U, P, I). `Failed`, the void and informing the customer come with Phase 3 |
| F-16 | Ticketing/fulfilment fails after confirmation | `FulfilmentFailed`; retry within TTL; else cancel + void/refund; alert | U, I | Planned |
| F-17 | Partial order (one item confirmed, another failed) | Order `PartiallyConfirmed`; capture confirmed items only; customer informed | U, I, E | Planned |

## Payments
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-20 | Card declined / authorization failed | Item stays bookable until offer expiry; no supplier call | U, A, E | Domain done (Phase 3 chunk 1): a decline does not move the item, which stays `AwaitingPayment` for another attempt until the offer expires, and booking is refused once the offer has expired (U). Payment port done (chunk 2): a decline is a definitive `Declined` outcome with a provider-neutral reason, and nothing is held (U, P, I). The payment attempt record comes with the orchestration Checkout done (Phase 3 chunk 3): a declined attempt is final, the order stays `AwaitingPayment`, and a new key starts a new attempt (U, I). **Generic declines (row 4d2):** the reason stays in Payments (the attempt and its history); checkout shows only `Declined` (U: AuthorizeCheckoutHandlerTests, AuthorizeOrderPaymentHandlerTests; I: OrderPaymentAuthorizationTests). Repeated attempts are capped (`Payments:AttemptLimits`, U) |
| F-21 | SCA challenge abandoned | Payment `Failed` after timeout; no booking | U, E | Partly done (Phase 3 chunk 2, U and P): a required challenge is `ActionRequired`, never `Authorized`, nothing can be captured, voiding it cancels it, and the port has a `Canceled` state. Expiring abandoned challenges, and the E2E level, come with the orchestration Checkout done (Phase 3 chunk 3): the challenge's customer action is returned, the order is not booked, and the same key looks the attempt up afterwards (U, I). The timeout of an abandoned challenge comes with reconciliation. Not yet: an abandoned challenge stays ActionRequired and keeps the order's one live payment slot until the reconciliation job (which must include ActionRequired in its work list) cancels or voids it: a gate for exposing checkout. Done (row 4): an unfinished challenge on an order that will not book is canceled by the release (U); a challenge is looked up every `LookupAfter` meanwhile (U). |
| F-22 | **Payment authorized, booking failed** | Void authorization (idempotent); customer sees failure with no charge | U, I, E | Payment port done (Phase 3 chunk 2): voiding an authorized hold is idempotent by key, and a captured payment cannot be voided (U, P, I). Driving the void from a `Failed` item comes with the orchestration Checkout (Phase 3 chunk 3): an authorization the order will not book on (offer expired meanwhile, or another amount) is refused as `AuthorizedButNotBookable` and noted once on the order's timeline with its reference (U). The void itself is a gate for exposing checkout. Release done (row 4): the unused hold is voided once, keyed by the attempt; a void that timed out is looked up and repeated with the same key only while still held; an interrupted void is looked up on restart (U, I). After booking failures the same release applies once supplier booking is wired. |
| F-23 | Booking confirmed, capture failed | Idempotent capture retry within validity; then `ManualReview` + alert; booking not auto-cancelled | U, I | Payment port done (Phase 3 chunk 2): a capture that timed out after capturing is `Unknown`, and a retry with the same key returns it, captured once (U, P). The retry window, `ManualReview` and the alert come with the orchestration |
| F-24 | Authorization expired before capture | Alert; policy decision (re-authorize or cancel) | U | Port ready (Phase 3 chunk 2): a lapsed hold is the `Expired` state, classified as `AuthorizationExpired` (U). The alert, the guard and the policy decision come with the orchestration |
| F-25 | Process crash between supplier confirm and capture | On restart, the Worker completes capture from outbox/timeline state | I | Planned |
| F-26 | Payment attempt stuck in `ManualReview` | Resolved only by a provider lookup (operator path): to what the provider holds, never what someone states; idempotent; operator and reason on the history; a payment the provider no longer finds stays in review | U | Done for payments (row 4d2; U: PaymentReviewTests). The admin endpoint, audit record and SQL-level test come with staff identity |

## Duplicates, ordering, and concurrency
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-30 | **Duplicate booking request** (same Idempotency-Key) | Original result returned; exactly one supplier booking | A, C | Order creation done (Phase 3 chunk 1): the same key and selection return the original order, sequential and parallel, enforced by a unique key index (U, I, C). Supplier level: at most one booking per client reference (chunk 6, P). The HTTP Idempotency-Key header comes with the endpoint (Q8) Payment attempts done (Phase 3 chunk 3): the same key returns the same attempt, sequential and parallel (U, I, C); a timed-out attempt is looked up, never authorized again. **HTTP done (row 4c):** `POST /api/v1/orders` with `Idempotency-Key`; a replay is 200 with the same order, and parallel requests with one key create one order (A, C). |
| F-31 | Same key, different payload | 409 `idempotency-conflict` | A | Order creation done (Phase 3 chunk 1): the same key for another selection gives `IdempotencyKeyReused` (U, I). The 409 mapping comes with the endpoint Payment attempts done (Phase 3 chunk 3): the same key with another amount or customer is `IdempotencyKeyReused` (U, I). **HTTP done (row 4c):** 409 `idempotency-conflict`; keys are per customer (A). |
| F-32 | Double-click / two tabs (different keys, same draft order) | Exactly one booking via state machine + `rowversion`; second gets 409 | C, E | Partly done (Phase 3 chunk 1): different keys for one selection create exactly one order (a unique selection index; U, I, C), and concurrent order changes are refused by rowversion (I). The 409 mapping and E2E come with the endpoint Payments done (Phase 3 chunk 3): at most one attempt per order may hold funds (filtered unique index), so a second key is `PaymentInProgress` while the first is live, and allowed after a decline (U, I); an order already authorized returns its booking, with no second hold (U). A replay after a timeout finishes the attempt made with that key before the supplier is asked again, so its hold is never unreachable (U); parallel requests with different keys create one attempt (C, I). **HTTP done (row 4c):** a second key for an ordered selection is 409 `selection-already-ordered` with the caller's own order id (never another customer's); parallel requests with different keys create one order (A, C). |
| F-33 | **Duplicate webhook** | Inbox unique constraint; second delivery is a no-op | I, C | Done for payments (ADR 0006): one row per provider event (unique constraint); a repeat delivery, even concurrent, is acknowledged and stored once (I: PaymentNotificationTests) |
| F-34 | Out-of-order webhooks | Stale transitions ignored; final state correct | U, I | Done for payments: a notification only prompts a lookup; a late event leaves a settled attempt unchanged (I). Mapping of each provider state: U (StripePaymentProviderTests) |
| F-35 | Invalid webhook signature | 400; not persisted; security event | A | Done for payments: missing, forged, tampered, downgraded (non-v1) or stale signatures are refused before anything is stored, with a security event (U: Stripe verification; I: 400 and nothing stored) |
| F-36 | **Duplicate refund request** | Existing refund returned; total refunded ≤ captured | A, C | Provider level done (Phase 3 chunk 2): the same refund key never refunds twice, the total refunded never exceeds the amount captured, and a key reused for another operation is an idempotency conflict (P). Refund records and the API come with the refund story |
| F-37 | Concurrent admin edits (e.g. markups) | ETag / `rowversion`: 412/409, no lost update | A, C | Planned |
| F-38 | Webhook and reconciliation poll race on the same payment | Single transition; the other is a no-op | C | Done for payments: the notification job and two reconciliations racing on one challenged payment make exactly one Authorized transition (C: PaymentNotificationTests) |

## Cancellation and refunds
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-40 | Cancellation within free period / void window | Supplier cancel → full refund | U, P, E | Planned |
| F-41 | Cancellation with penalty | Quote shown and accepted → cancel → partial refund | U, P, E | Planned |
| F-42 | Cancel call times out | `CancellationPending`; reconcile; no resubmit unless the supplier guarantees idempotency | U, P | Planned |
| F-43 | Refund fails at Stripe | Refund `Failed`; alert; ops retry with the same key | U, I | Provider level done (Phase 3 chunk 2): a refund refused before processing succeeds when retried with the same key (P). The refund record, `Failed` and the alert come with the refund story |
| F-44 | Partial refund then dispute | State consistent; evidence available from the timeline | U | Planned |

## Supplier-initiated changes (later phases)
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-50 | Airline schedule change | Booking flagged; customer notified; accept/refund options; ops queue | U, P, E | Planned |
| F-51 | Hotel confirmation number arrives later | Booking updated; voucher re-issued | U, P | Planned |
| F-52 | Hotel cannot honour the booking (walk) | Ops workflow; customer contact; refund/compensation | U | Planned |

## Security
| ID | Scenario | Expected behaviour | Levels | Status |
|---|---|---|---|---|
| F-60 | Customer accesses another customer's order | 404 (no existence leak); security event | A | Done for orders (row 4c) and travellers and documents (row 4f): 404 with nothing revealed (A: CustomerOrderEndpointTests, OrderTravellerEndpointTests). The security event on repeated misses is planned |
| F-61 | Staff without permission attempts an admin action | 403; audited | A | Planned |
| F-62 | Endpoint without an authorization policy | Architecture/API test fails the build | A | Planned |
| F-63 | The supplier starts (or stops) requiring travel documents at revalidation; travellers change during payment | Recorded on the order timeline. Checkout refuses payment until each traveller's document is given, and no document is accepted when not required. Travellers are frozen while a payment is live. A resumed or raced payment whose travellers are incomplete releases its hold and books nothing | U, A | Done (row 4f; U: AuthorizeCheckoutHandlerTests, PersonalDataTests; A: OrderTravellerEndpointTests; P: mock ZDR and Amadeus `documentRequired`) |
| F-64 | Document encryption key missing, wrong or ciphertext tampered with | Nothing stored without a key (503 `documents-unavailable`); a moved or altered ciphertext never decrypts; a read releases data only after its audit row is saved | U | Done (row 4f; U: PersonalDataTests) |
| F-65 | Personal data due for purge, of an abandoned order, or under legal hold | Documents shredded after last flight + 30 days, travellers and contact anonymised after + 25 months, idempotently. An abandoned order (never booked, no payment unsettled) has its documents shredded at once and its personal data anonymised 30 days later, from an `OrderAbandoned` event consumed once. A held set is neither purged nor changed until released | U, I | Done (row 4f; U: PersonalDataTests, ExpireUnpaidOrderHandlerTests; I/A: OrderTravellerEndpointTests, including abandonment end to end) |
| F-66 | Card testing: repeated payment attempts | Attempt limits refuse with a generic message, and the order stays AwaitingPayment. Refusals are recorded once per key, and the refusal stands even if recording fails. Reaching the threshold raises `PaymentAttemptLimitRepeated` once and lists the customer for review, with no automatic block. Per-customer write limit and per-address limit before authentication (429) | U, A | Done (row 4f; U: AuthorizeOrderPaymentHandlerTests; A: OrderTravellerEndpointTests rate limits). The checkout endpoint limit applies when checkout is exposed |
