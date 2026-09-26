# Progress

_Last updated: 2026-09-26 (Phase 3: first vertical slice, in progress)_

## Current phase: 3 — First vertical slice (flights), in progress

**Q1 answered 2026-09-25: we are merchant of record for flights (Option A).** **Q8 answered 2026-09-26: customers sign in before booking (no guest checkout).** Hotels (Q1), markets (Q2), currencies and FX (Q5), suppliers (Q6), retention (Q9), fraud (Q10), refund thresholds (Q11) and group or child-only bookings (Q13) stay open. **ADR 0005 was accepted for flights**, since its Q1 gate is now met. **ADR 0006 stays Proposed**: accepting it also fixes the payment provider (Stripe), which depends on Q2 and Q5.

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
| 4c | **Next (audit):** identity and the customer booking path (token validation, internal customer id, customer endpoints, supplier booking then capture) | **Blocked on decisions:** the identity provider tenant (ADR 0008), Q9 (traveller data), and ADR 0006 for card entry |
| 5 | Supplier booking after authorization (`FlightSupplierBooking` behind a Flights Contracts entry point), then capture or void | **Blocked on traveller data:** names are PII, documents Sensitive PII, retention is Q9 |

**Preconditions for any payment endpoint** (security review, chunk 2):
- Bind the payment-method token as a string in the public `*Request` and build `PaymentMethodToken` in the handler, so the result is a 400, not a 500.
- Validation problems never echo the value, and request/body logging excludes the route.
- A permission-matrix test covers cross-customer authorize, capture, void and refund.
- `*Details` records are never logged or serialized.
- In Production, `PaymentOperations` must fail at startup without a provider.

**Preconditions for any Orders or checkout endpoint:** Q8 is answered and the order owner, per-customer keys and hidden foreign order ids are done (chunk 3). Still needed:
- ADR 0008 accepted with a configured identity provider; the customer id taken from the token only.
- The IdP subject mapped to an internal customer id (it is pseudonymous PII in append-only rows; security review, chunk 3).
- An audited operator way out of ManualReview payment attempts (release and reconciliation are done, row 4), and alerting on the conditions in `docs/runbooks/payment-hold-release.md`.
- A cap on payment attempts per order and customer, a per-customer rate limit, generic declines to clients and a velocity security event (card testing; fraud policy, Q10).
- The HTTP permission matrix, including cross-customer attempts.
- Create `Modules.Orders.Contracts` with its first consumer.

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
