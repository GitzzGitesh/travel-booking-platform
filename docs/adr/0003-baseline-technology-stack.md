# 0003. Baseline technology stack

- **Status:** Proposed
- **Date:** 2026-09-24
- **Related:** [0009](0009-frontend-applications-and-rendering.md), [0011](0011-caching.md), `docs/quality/testing-strategy.md`

## Context

The initial direction was Angular, ASP.NET Core/C#, SQL Server/Azure SQL, EF Core, Redis, Azure, REST/OpenAPI. We need to pin versions and choose supporting libraries while avoiding licensing traps and unnecessary dependencies. The dev machine has .NET SDK 10.0.401 and Node 24 installed; Docker is not installed yet.

## Decision

| Area | Choice |
|---|---|
| Runtime | **.NET 10 (LTS)**, SDK pinned via `global.json`; C# latest |
| Web | ASP.NET Core (minimal APIs or controllers, decided at scaffold; either way thin) |
| OpenAPI | Built-in `Microsoft.AspNetCore.OpenApi` + Scalar UI (non-production). No Swashbuckle |
| Data | SQL Server / Azure SQL, EF Core 10 |
| Validation | Hand-written or FluentValidation (Apache-2.0), decided at scaffold |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8 underneath) |
| Observability | OpenTelemetry SDK → Azure Monitor (Application Insights) |
| Local orchestration | **.NET Aspire AppHost** (SQL Server container, later Redis; OTel dashboard). Fallback: docker-compose |
| Frontend | Angular (current major at scaffold time), TypeScript strict, Angular CLI workspace (no Nx) |
| Tests (.NET) | xUnit v3, **Shouldly** (or AwesomeAssertions), Testcontainers, ArchUnitNET/NetArchTest, `FakeTimeProvider` |
| Tests (web) | Angular default unit runner (Vitest), Playwright, axe-core |
| CI/CD | GitHub Actions |
| IaC | Bicep (decided in a later ADR, at first deployment) |
| Container runtime (dev) | Docker Desktop or Podman, **required from Phase 1** for integration tests |

**Not used** (commercial licensing for new versions, or unnecessary): MediatR, AutoMapper, FluentAssertions v8+, MassTransit v9+, Swashbuckle, Nx. Application handlers are plain classes; mapping is written by hand.

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
| Pact for contract tests | One consumer, same team. OpenAPI diff + provider contract suites address the real risk (see testing strategy) |
