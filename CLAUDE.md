# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Global travel booking platform: customers search, book, and pay for flights and hotels, manage bookings and travellers, receive tickets/vouchers, cancel, and track refunds. An Admin/Operations portal covers customers, bookings, payments, refunds, providers, pricing/markups, promotions, reports, audit, and users/roles/permissions. B2B/agent features may come later.

**Current phase: see `docs/progress.md`.** Phase 3 (first vertical slice, flights) is in progress. The Flights module (search, selection, revalidation, supplier booking port) and the Orders module foundation exist. Q1 is answered for flights (we are merchant of record), and ADR 0005 is accepted for flights. There is no real supplier (Q6), no accepted payment ADR (0006), and no market or hosting decision (Q2). Check `docs/progress.md` for the current phase and story before starting any work. **Do not scaffold apps, add dependencies, create migrations, or write business code unless the current story explicitly says so.**

## Commands

Backend (.NET 10 SDK pinned in `global.json`; tests run on Microsoft.Testing.Platform, xUnit v3):
- Build: `dotnet build TravelBooking.slnx` (warnings are errors)
- All tests: `dotnet test --solution TravelBooking.slnx`
- One project: `dotnet test --project tests/backend/ArchitectureTests`
- Single test: `dotnet test --project tests/backend/Api.IntegrationTests -- --filter-method "*One_way_search_returns_priced_offers_with_local_times"`
- Format check: `dotnet format TravelBooking.slnx --verify-no-changes`
- Run the Api: `dotnet run --project src/backend/Hosts/Api` (Development, http://localhost:5080; OpenAPI at `/openapi/v1.json`, served in Development only)
- API contract: `src/backend/Hosts/Api/openapi.v1.json` is enforced by `OpenApiContractTests`. After reviewing an intended contract change, regenerate it with `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --project tests/backend/Api.IntegrationTests` and commit it.
- Database (SQL Server, one schema per module): the Orders module owns schema `orders` (connection string `Orders`; migrations as for Flights with `--project`/`--startup-project src/backend/Modules/Orders/Modules.Orders`). The Flights module owns schema `flights` (connection string `Flights`, set with user-secrets locally). Migrations use the local tool (`dotnet tool restore`): `dotnet ef migrations add <Name> --project src/backend/Modules/Flights/Modules.Flights --startup-project src/backend/Modules/Flights/Modules.Flights --output-dir Infrastructure/Migrations`, then review `dotnet ef migrations script`. Migrations are never applied at startup.
- Database-backed tests (`Modules.Flights.IntegrationTests`, and the `SqlApiFactory` classes in `Api.IntegrationTests`: selection, revalidation, supplier booking and order creation) use Testcontainers and need Docker. If Docker is only reachable inside WSL, run them there with a separate output path: `dotnet test --project tests/backend/<Project> --artifacts-path /tmp/tb-artifacts`.

Frontend (run in `src/frontend`):
- Install: `npm ci`
- Build: `npx ng build customer-web` (SSR + prerender) / `npx ng build admin-web` (SPA), then `npm run check:admin-web-csp`; `npm run check:app-boundaries` (apps never import each other)
- Unit tests (Vitest): `npx ng test customer-web --watch=false` / `npx ng test admin-web --watch=false`
- Dev server: `npx ng serve customer-web` / `npx ng serve admin-web`. customer-web proxies `/api` to the Api on http://localhost:5080 (`proxy.conf.json`), so run the Api too.
- API client: `npm run generate:api-client` after the contract snapshot changes (commit the result); `npm run check:api-client` type-checks it (ADR 0013)

CI (`.github/workflows/ci.yml`, `codeql.yml`) runs these same backend and frontend steps (including the API-client drift check), an oasdiff breaking-change check on PRs, a gitleaks history scan, and CodeQL on every PR and push to `main`.

E2E and accessibility (`tests/e2e`, Playwright + axe-core, against the production builds; Playwright also starts the Api with `dotnet run` in Development): build both apps first, then in `tests/e2e`: `npm ci`, `npx playwright install chromium` (once), `npx playwright test` (`--project admin-web` for one app).

Database-backed E2E journeys (offer selection) need `E2E_FLIGHTS_DB`: a connection string to a local SQL Server with the Flights migrations applied (`dotnet ef database update ... --connection "$E2E_FLIGHTS_DB"`, local databases only). Without it they are skipped locally and fail in CI, which provisions its own. The E2E Api listens on 5099, never the dev port 5080. If SQL Server runs in WSL, connect via the WSL address (`wsl hostname -I`), not `localhost`.

Not yet available: Aspire AppHost run (deferred; see `docs/progress.md`, chunk 4b).

Also:
- Test the secret-guard hook: `echo '{"tool_name":"Write","tool_input":{"file_path":"x.txt","content":"hello"}}' | node .claude/hooks/guard-secrets.mjs` (exit 0 = allowed, 2 = blocked)

## Target architecture (defined by the ADRs in `docs/adr/`; each ADR states its own status, Proposed or Accepted)

- **Backend**: ASP.NET Core on .NET 10, **modular monolith**. Two hosts from one codebase: `Api` and `Worker` (outbox dispatch, webhooks, reconciliation, expiry jobs). Each module = `Modules.X` (Domain/Application/Infrastructure *folders*) + `Modules.X.Contracts` (the only thing other modules may reference). Boundaries are enforced by architecture tests. See ADR 0002 and 0007.
- **Suppliers**: `IFlightProvider`, `IHotelProvider`, `IPaymentProvider` ports live in the core; each supplier is a separate `Integrations.*` project mapping supplier DTOs to domain types (anti-corruption layer). Deterministic, scenario-driven mock providers come first. See ADR 0004 and `docs/architecture/provider-integration.md`.
- **Booking**: an `Order` aggregate holds `OrderItems` (flight/hotel bookings); payments attach to the Order. Flow is **authorize → book → capture** (void on failure); unknown supplier outcomes go to `PendingConfirmation` and are resolved by reconciliation, never by resubmission. See ADR 0005 and `docs/architecture/booking-lifecycle.md`.
- **Payments**: Stripe (Payment Intents, manual capture, Elements, so card data never reaches our servers) behind `IPaymentProvider`, plus a mock. See ADR 0006 and `docs/architecture/payment-lifecycle.md`.
- **Data**: SQL Server / Azure SQL with EF Core, one schema per module, SQL transactional outbox, and no message broker. HybridCache, with Redis added later as L2.
- **Frontend**: one Angular workspace with `customer-web` (SSR) and `admin-web` (SPA), consuming only the OpenAPI-generated client. See ADR 0009.
- **Identity**: external IdP (Entra External ID for customers, Entra ID for staff). Permission-based authorization lives in the app. See ADR 0008.

## Non-negotiables (details in `.claude/rules/`)

1. Never bypass module boundaries. Cross-module access goes only through `*.Contracts`, and no module touches another module's tables.
2. Supplier/provider DTOs never leave `Integrations.*` projects.
3. Prices are computed and revalidated server-side from persisted offers. Client-sent prices are never trusted.
4. Booking, payment, and refund commands are idempotent (idempotency key + DB unique constraint).
5. Never blindly retry supplier booking/ticketing or payment writes. An unknown outcome means reconcile, not resubmit.
6. State changes go through explicit state machines and append to the booking timeline.
7. Raw card data (PAN/CVV) never touches our servers, logs, or test fixtures.
8. Secrets and provider credentials never go in Git, appsettings, or client code. Use Key Vault / user-secrets.
9. PII is classified, redacted in logs, and document numbers are encrypted at rest.
10. Every endpoint declares an authorization policy (deny by default). Admin actions are audited.
11. The frontend never calls travel suppliers directly.
12. Money = `decimal` + ISO-4217 currency. Instants are stored in UTC; supplier local times keep their IANA zone.

## Working agreement

- Before changing code, read the relevant ADRs in `docs/adr/`, the architecture docs, and `docs/quality/failure-scenarios.md`.
- **Do not silently change major architecture.** Raise concerns in this format and wait for a decision:
  ```
  ARCHITECTURE REVIEW
  Current assumption:
  Concern:
  Recommendation:
  Reason:
  Impact:
  Decision:
  ```
- Prefer the simplest design that is safe. New projects, hosts, infrastructure, libraries, cloud services, agents, or MCP servers require an ADR. The three read-only reviewer agents in `.claude/agents/` belong to the approved foundation and need no ADR unless their tools or authority change.
- Stay within the story's scope. Do not modify unrelated files or reformat untouched code.
- **Verify before claiming.** Run the relevant build/tests and report the actual output. Never say "should work".
- Update `docs/progress.md`, and any affected docs/runbooks/ADRs, in the same change.
- Ask before adding dependencies, committing, or pushing.

## Where things live

| Path | Purpose |
|---|---|
| `.claude/rules/` | Focused engineering rules. Global rules load always; path-scoped rules load with matching files |
| `.claude/skills/` | `travel-domain` (domain reference), `implement-story` (delivery workflow), `production-review` (change review checklist) |
| `.claude/agents/` | Read-only reviewers: `architecture-reviewer`, `security-reviewer`, `booking-flow-reviewer` |
| `.claude/hooks/guard-secrets.mjs` | PreToolUse hook that blocks writing secrets or key files |
| `docs/requirements/` | Scope, non-functional requirements, glossary, open business questions |
| `docs/architecture/` | Overview, booking/payment lifecycles, provider integration, security, observability |
| `docs/adr/` | Architecture decisions (MADR-lite). Accepted ADRs are immutable; supersede them instead |
| `docs/quality/` | Testing strategy, failure-scenario catalog, definition of done |
| `docs/runbooks/` | Operational runbooks, written alongside the features they cover |

## Definition of Done (summary)

The change is within scope, the build is green, the relevant tests (including failure-path tests from the catalog) have been run with output shown, rules are respected, docs and `progress.md` are updated, and a reviewer agent or `production-review` has been run for booking, payment, security, or boundary-affecting changes. Full list: `docs/quality/definition-of-done.md`.

## Git

Trunk-based: short-lived `feat/…`, `fix/…`, `chore/…` branches, PRs into protected `main`, squash merge, Conventional Commits (`feat(orders): …`). See ADR 0012.
