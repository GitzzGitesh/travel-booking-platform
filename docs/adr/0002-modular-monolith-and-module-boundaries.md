# 0002. Modular monolith and module boundaries

- **Status:** Accepted. The inter-module communication rule is refined by [0015](0015-synchronous-cross-module-commands-for-checkout.md): narrowly scoped synchronous, idempotent commands for checkout orchestration.
- **Date:** 2026-09-24
- **Related:** [0004](0004-supplier-provider-abstraction.md), [0007](0007-async-processing-worker-and-outbox.md), `docs/architecture/overview.md`, `.claude/rules/architecture.md`

## Context

The platform spans search, orders, bookings, payments, pricing, customers, access control, notifications, audit, and reporting. The team is small (see Q4), and the domain is still being learned. Microservices would add distributed-system cost (network failures, distributed transactions, deployment and observability overhead) before we know the right boundaries. A traditional layered monolith tends to erode into a tangle.

Clean Architecture applied literally (Domain/Application/Infrastructure/API projects **per module**) would produce 40+ projects, slowing builds and adding ceremony without adding enforcement that tests cannot give.

## Decision

We will build a **modular monolith** in one .NET solution.

- Each module has **two projects**:
  - `Modules.X`: internal code in `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/` folders. Types are `internal` by default.
  - `Modules.X.Contracts`: the module's public surface (interfaces, DTOs, integration events). The **only** project other modules may reference.
- Each module owns its **database schema**. No cross-module joins or foreign keys.
- Inter-module communication: synchronous calls through Contracts interfaces for queries; **integration events via the outbox** for side effects.
- Layering inside a module (Domain has no infrastructure dependencies, etc.) is enforced by **architecture tests** that run in CI.
- Supplier adapters are separate `Integrations.*` projects (see ADR 0004), so supplier SDKs are physically isolated.
- Two hosts (`Api`, `Worker`) compose the modules (see ADR 0007).
- Modules are created when a story needs them, not up front.

## Consequences

**Positive**
- One deployment unit per host, local transactions, simple debugging.
- Clear seams: a module can be extracted into a service later if scaling or team structure demands it.
- Boundaries are enforced by tests, not by convention.

**Negative / trade-offs**
- Discipline required. Architecture tests must be written early (Phase 1).
- A shared database server means noisy-neighbour risk between modules (acceptable at this scale).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Microservices | Premature distributed complexity; boundaries unknown; high ops cost |
| 4 projects per module (strict Clean Architecture) | Project explosion and ceremony; architecture tests give equal enforcement |
| Single project, folders only | Too easy to violate boundaries; no Contracts seam |
