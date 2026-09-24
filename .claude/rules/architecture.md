# Architecture rules

Applies to all work. Background: ADR 0002 (modular monolith), 0004 (providers), 0007 (async), `docs/architecture/overview.md`.

## Boundaries
- A module may reference another module **only** through its `Modules.X.Contracts` project (public interfaces, DTOs, integration events). Never reference another module's internal types.
- A module owns its database schema. **No cross-module joins, queries, or foreign keys.** Get data through contracts or keep a local read copy fed by integration events.
- Inside a module: Domain has no dependencies on Application, Infrastructure, EF Core, ASP.NET, or HTTP. Application depends on Domain only (plus BuildingBlocks). Infrastructure implements Application ports.
- Supplier/provider DTOs, SDK types, and error codes stay inside their `Integrations.*` project. They are mapped to domain types at the adapter boundary (anti-corruption layer).
- The core depends on `IFlightProvider` / `IHotelProvider` / `IPaymentProvider` ports, never on a concrete supplier.
- The frontend talks only to our API. It never calls travel suppliers or payment APIs directly, except Stripe.js/Elements for card entry.

## Endpoints
- Endpoints/controllers stay thin: bind → authorize → call one application handler → map the result to HTTP. No business rules, pricing, or EF queries in endpoints.

## Complexity budget
- New projects, hosts, infrastructure, message brokers, libraries, cloud services, agents, or MCP servers require an ADR first.
  - Exception: the three initial reviewer agents (`architecture-reviewer`, `security-reviewer`, `booking-flow-reviewer`) are part of the approved engineering foundation and need no separate ADR. An ADR **is** required if any of them gains write, Bash, or MCP tools, becomes able to approve or block merges automatically, or if a new agent is added.
- Do not add abstractions without a second concrete use or a test-driven need. Provider ports are the deliberate exception.
- Use DDD tactical patterns (aggregates, value objects, domain events) for Orders, Bookings, Payments, Refunds, and Pricing. Reference data and admin lists can be simple CRUD.

## Changing the architecture
- Never change architecture silently. Raise an **ARCHITECTURE REVIEW** (format in CLAUDE.md) and wait for a decision. Accepted ADRs are superseded by new ADRs, not edited.
