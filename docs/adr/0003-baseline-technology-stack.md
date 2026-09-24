# 0003. Baseline technology stack

- **Status:** Accepted
- **Date:** 2026-09-24 (revised 2026-09-25: Phase 1 choices for web style, validation, assertions, and architecture tests)
- **Related:** [0009](0009-frontend-applications-and-rendering.md), [0011](0011-caching.md), `docs/quality/testing-strategy.md`

## Context

The initial direction was Angular, ASP.NET Core/C#, SQL Server/Azure SQL, EF Core, Redis, Azure, REST/OpenAPI. We need to pin versions and choose supporting libraries while avoiding licensing traps and unnecessary dependencies. The dev machine has .NET SDK 10.0.401 and Node 24 installed; Docker is not installed yet.

## Decision

| Area | Choice |
|---|---|
| Runtime | **.NET 10 (LTS)**, SDK pinned via `global.json`; C# latest |
| Web | ASP.NET Core **minimal APIs**: one `Map…Endpoints` extension per module, route groups for `/api/v1` and `/api/admin/v1` carrying authorization, rate-limit, and idempotency filters, `TypedResults` for accurate OpenAPI |
| OpenAPI | Built-in `Microsoft.AspNetCore.OpenApi` + Scalar UI (non-production). No Swashbuckle |
| Data | SQL Server / Azure SQL, EF Core 10 |
| Validation | **Built-in ASP.NET Core 10 minimal-API validation** (DataAnnotations + `IValidatableObject`) for request shape → `400 ValidationProblem`. Domain invariants live in value objects returning `Result`; business-rule failures → `422`. No FluentValidation (fallback only, via ADR amendment) |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8 underneath) |
| Observability | OpenTelemetry SDK → Azure Monitor (Application Insights) |
| Local orchestration | **.NET Aspire AppHost** (SQL Server container, later Redis; OTel dashboard). Fallback: docker-compose |
| Frontend | Angular (current major at scaffold time), TypeScript strict, Angular CLI workspace (no Nx) |
| Tests (.NET) | xUnit v3, **Shouldly**, Testcontainers, **ArchUnitNET**, `FakeTimeProvider` |
| Tests (web) | Angular default unit runner (Vitest), Playwright, axe-core |
| CI/CD | GitHub Actions |
| IaC | Bicep (decided in a later ADR, at first deployment) |
| Container runtime (dev) | Docker Desktop or Podman, **required from Phase 1** for integration tests |

**Not used** (commercial licensing for new versions, or unnecessary): MediatR, AutoMapper, FluentAssertions v8+, MassTransit v9+, Swashbuckle, Nx. Application handlers are plain classes; mapping is written by hand.

### Phase 1 choices (2026-09-25)

- **Minimal APIs, not controllers.** Endpoint handlers stay `internal`, as ADR 0002 requires; MVC discovers only public controllers. Route groups apply authorization policies, rate limiting, and the `Idempotency-Key` filter once per group. Endpoints stay thin (bind → one handler → `TypedResults`). Deny-by-default is enforced with a fallback policy plus an API test that lists every endpoint and fails if one lacks explicit authorization or anonymous metadata. Errors use `AddProblemDetails()` and a global exception handler.
- **Built-in validation, not FluentValidation.** No third-party dependency, failures map to `400 ValidationProblem` automatically, and DataAnnotations constraints flow into the OpenAPI document and the generated client. Async or data-dependent checks belong in handlers. The Phase 1 skeleton must confirm that the validation source generator covers endpoints mapped in module class libraries, and whether a first-party package reference is needed. If it does not work, FluentValidation (Apache-2.0) is the fallback, recorded by amending this ADR.
- **Shouldly, not AwesomeAssertions.** Clear failure messages for state, money, and exception assertions; small API; long-established and independently governed. Accepted trade-off: weaker object-graph comparison, so API tests assert specific fields of typed contracts.
- **ArchUnitNET, not NetArchTest.** Expresses the module rules (`*.Contracts`-only references, Domain free of EF Core/ASP.NET Core/HTTP, supplier types only in `Integrations.*`, internal-by-default) and detects cycles between modules. Actively maintained (Apache-2.0). The architecture is loaded once per run in a shared fixture. The Phase 1 skeleton confirms xUnit v3 integration; if there is none, rules run through the core library's check with a Shouldly assertion.

Each package is still added only when Phase 1 needs it, with the approval and licence check required by `.claude/rules/workflow.md`.

New libraries require justification and a licence check (`.claude/rules/workflow.md`), and an ADR if significant.

## Consequences

**Positive:** LTS runtime to Nov 2028; no licence surprises; fewer dependencies; built-in OpenAPI tracks the framework.
**Negative:** slightly more hand-written code (mapping, handler plumbing); Docker becomes a prerequisite earlier than planned.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| .NET 8 | Support ends Nov 2026. Starting on it would force an early upgrade |
| PostgreSQL | Viable, but the team direction is SQL Server/Azure SQL; no compelling reason to change |
| docker-compose instead of Aspire | Kept as fallback; Aspire adds a free local telemetry dashboard and .NET-native configuration |
| Controllers | Must be public for discovery, which conflicts with internal-by-default; controller base classes attract logic |
| FluentValidation | Extra dependency; rules don't flow into OpenAPI; built-in validation covers boundary shape checks |
| AwesomeAssertions | Younger fork (created after FluentAssertions went commercial); its richer API isn't needed |
| NetArchTest | Original is barely maintained and the ecosystem is split across forks; no direct module-cycle rule |
| Pact for contract tests | One consumer, same team. OpenAPI diff + provider contract suites address the real risk (see testing strategy) |
