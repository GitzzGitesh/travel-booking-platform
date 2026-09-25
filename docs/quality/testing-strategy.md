# Testing strategy

**Status: Proposed.** Tooling choices are part of ADR 0003.

## Principles
1. **Risk-weighted, not coverage-driven.** The heaviest testing goes where money and bookings can go wrong: state machines, idempotency, provider failure paths, concurrency.
2. **Deterministic.** Mock providers with explicit scenarios, `FakeTimeProvider` for time, no real suppliers in PR builds.
3. **Real infrastructure where it matters.** Integration tests use a SQL Server container (Testcontainers), never the EF InMemory provider or SQLite.
4. **Every failure scenario has an owner test.** The canonical list is [`failure-scenarios.md`](failure-scenarios.md).
5. **Mock parity.** Mocks and real adapters pass the same provider contract suite.

## Test levels
| Level | Location (planned) | Tooling | What it covers | Runs |
|---|---|---|---|---|
| Domain unit | `tests/backend/Modules.X.UnitTests` | xUnit v3, Shouldly | Aggregates, every legal and illegal state transition, money/rounding, pricing math | Every PR |
| Application | same project | xUnit, mock providers, `FakeTimeProvider` | Handlers/orchestration, idempotency logic, compensation | Every PR |
| Architecture | `tests/backend/ArchitectureTests` | ArchUnitNET (xUnit v3) | Module boundaries, no supplier types in core, Domain purity | Every PR |
| Integration | `tests/backend/Modules.X.IntegrationTests` | Testcontainers (SQL Server) | EF mappings, migrations apply, `rowversion` conflicts, unique idempotency constraints, outbox/inbox | Every PR |
| API | `tests/backend/Api.IntegrationTests` | `WebApplicationFactory`, Testcontainers | HTTP contract, ProblemDetails types, every endpoint declares authorization, authorization matrix, `Idempotency-Key` behaviour | Every PR |
| API contract | CI step | OpenAPI snapshot + oasdiff | No breaking changes within a version | Every PR |
| Provider contract | `tests/backend/ProviderContracts` | xUnit shared suites per port | Each provider (mocks on every PR; real sandboxes nightly) honours port semantics, error taxonomy, retrieve-by-reference | PR (mocks) / nightly (sandboxes) |
| Concurrency | integration/API projects | Parallel tasks against the real DB | Duplicate/parallel book, capture, refund, cancel lead to exactly one effect | Every PR |
| Frontend unit | `src/frontend/**` | Angular default runner (Vitest) | Components, services, form validation, idempotency-key handling | Every PR |
| E2E | `tests/e2e` | Playwright | Critical journeys + injected failure scenarios against Api/Worker with mocks | PR (smoke) / nightly (full) |
| Accessibility | `tests/e2e` | Playwright + axe-core | WCAG 2.2 AA checks on key pages | Every PR (key pages) |
| Security | CI | Dependabot, gitleaks, CodeQL; OWASP ZAP baseline | Dependencies, secrets, SAST, DAST | PR / pre-release |
| Performance | `tests/perf` | k6 | Search latency and throughput, supplier fan-out | Pre-release |

## Mock provider scenarios
See [`provider-integration.md`](../architecture/provider-integration.md#mock-providers). Tests select scenarios explicitly. E2E tests use the same scenarios through the UI.

## Test data
Synthetic travellers only. Stripe test cards/tokens only. No production data in any non-production environment.

## CI gates (from Phase 1)
Build (warnings as errors) · format check · unit + architecture + integration + API tests · OpenAPI diff · frontend lint + unit · Playwright smoke · gitleaks · CodeQL. The PR cannot merge unless all pass.
