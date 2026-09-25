# Progress

_Last updated: 2026-09-25 (Phase 2: Flights slice)_

## Current phase: 2 — Flights slice (in progress)

Phase 1 is **complete**. Q3 was answered on 2026-09-25: **flights first**. The direction is: flight search UI → API → Flights module → `IFlightProvider` → deterministic mock → results. No real supplier (Q6), payment design (Q1), or market or hosting decision (Q2) is assumed.

### Phase 2: plan
| # | Chunk | Status / depends on |
|---|---|---|
| 1 | Flight **search port** (`IFlightProvider.SearchAsync`, with its types in `Modules.Flights.Ports`). `BuildingBlocks`: `Money`, `CurrencyCode`, `Result`, and the provider error taxonomy. Deterministic **mock provider** (`Integrations.Flights.Mock`: XTS test currency, carrier ZZ, scenarios selected by configuration). **Provider contract suite** (`tests/backend/ProviderContracts`). Architecture rules for ports and adapters | **Done** (ADR 0014 Accepted). The mock refuses Production and undefined scenarios at startup. `ProviderOfferRef.Value` is an opaque adapter token |
| 2 | Flight search **API endpoint**: validation, mapping provider errors to ProblemDetails, contract snapshot and client. The host registers the mock outside Production. Delete `Modules.Sample` (oasdiff ignore file, ADR 0013). Development-only until rate limiting exists | **Done.** `POST /api/v1/flights/searches` (anonymous, Development-only). Provider errors map to 503 `provider-unavailable`, 422 `search-rejected`, or 502 `provider-error`. No offer id is exposed until offer selection. Dates are bounded between yesterday (UTC) and a 361-day sales horizon (a supplier constraint to revisit with Q6). Cabin accepts documented names only. The mock runs only in Development or Staging. `Modules.Sample` is deleted; its removal is accepted in `src/backend/Hosts/Api/openapi-accepted-breaking-changes.txt` |
| 3 | customer-web **search UI and results** through the generated client, with a Playwright journey test and axe checks | **Done.** The search page is the home route: form, validation, loading, empty and error states, results, and client-side offer selection. It calls the generated client on the same origin (dev proxy; the deployed gateway is part of the hosting story). Server routes are per route (search prerendered, all other routes client-rendered). Playwright covers the journey against the real Api and mock, plus empty, outage, invalid-input, and phone-width cases, all with axe |
| 4a | **Offer selection backend** (Option 2). Search results are held in HybridCache under a random `searchId` with per-offer ids (TTL no later than the earliest offer expiry; keys carry no PII). `POST /api/v1/flights/selected-offers` persists **only** the selected offer's supplier-neutral snapshot in SQL Server (EF Core, schema `flights`, migration `InitialFlights`). Selection is idempotent (unique `(SearchId, OfferId)`: 201, then 200) and F-02 (expired or evicted search, unknown offer, expired offer) returns 422 `offer-expired`. Testcontainers integration tests; OpenAPI and client updated | **Done** |
| 4b | customer-web **selection flow** calling `selected-offers` (with expired-offer handling), the **Aspire AppHost** (Api + SQL Server) for local runs, and E2E with a real database | Next |
| 5 | **Revalidation** (`RevalidateAsync`): F-01 price changed, F-02 offer expired, F-03 sold out | After 4 |

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
  - Forwarded headers, then per-client rate limiting via `.RequireRateLimiting(...)` in `MapFlightsEndpoints` on both endpoints, plus an endpoint-metadata test that every anonymous `/api/v1` endpoint is rate limited.
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
- Exposing search in production needs forwarded headers and rate limiting, which need **Q2** plus the hosting decision.
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
| Q1 Merchant of record | Payment architecture is finalised; Phase 2 payment work (`IPaymentProvider`) | ADR 0005, ADR 0006 |
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
