# 0004. Supplier provider abstraction and anti-corruption layer

- **Status:** Accepted
- **Date:** 2026-09-24
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0005](0005-order-aggregate-and-booking-orchestration.md), `docs/architecture/provider-integration.md`

## Context

We will integrate multiple suppliers over time (candidates: Duffel, Amadeus, Sabre, Travelport, Hotelbeds, Expedia). Their models differ fundamentally: NDC offer/order vs GDS PNR/ticket vs hotel rate/prebook. Error semantics, idempotency support, and timeouts differ too. If supplier models leak into the core, every new supplier becomes a core rewrite. Development and testing must work before any supplier contract exists.

## Decision

- The core defines ports: **`IFlightProvider`** (Flights module), **`IHotelProvider`** (Hotels module), **`IPaymentProvider`** (Payments module).
- The port model is based on **offer → order** semantics (search → offer → revalidate → book → retrieve → cancel), which maps well to NDC and can be adapted to GDS flows inside adapters.
- Each supplier is an `Integrations.<Area>.<Supplier>` project that maps supplier DTOs to domain types. **Supplier types never cross the adapter boundary.**
- Ports return a shared **error taxonomy** (`PriceChanged`, `OfferExpired`, `SoldOut`, `InvalidRequest`, `Rejected`, `Unknown`, `Unavailable`, `RateLimited`, `AuthFailure`).
- Every port must support **retrieve by our client reference** so that unknown outcomes can be reconciled.
- Each operation declares whether it is idempotent at the provider. Resilience policies are selected from that (no retries on non-idempotent writes).
- **Mock providers are built first**, are deterministic and scenario-driven, and are registered only in non-production environments.
- A **provider contract test suite** per port runs against every implementation, mocks included.
- Adding a real supplier requires its own ADR (commercial model, merchant model, idempotency and retrieve support, sandbox).

## Consequences

**Positive:** suppliers are swappable and addable; the core is testable without suppliers; failure behaviour is uniform.
**Negative:** mapping effort per supplier; risk of a lowest-common-denominator port. Mitigation: validate the port against two real supplier shapes (one NDC-style, one GDS or bed bank) before freezing it.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Use one aggregator's model as the domain model | Couples the core to one supplier; painful when adding a second |
| Generic "travel product" port for flights and hotels | Flights and hotels differ too much; a shared port would be vague and leaky |
