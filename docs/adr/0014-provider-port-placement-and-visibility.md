# 0014. Provider port placement and visibility

- **Status:** Accepted
- **Date:** 2026-09-25
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0004](0004-supplier-provider-abstraction.md), `docs/architecture/provider-integration.md`, `.claude/rules/architecture.md`

## Context

ADR 0004 puts provider ports (`IFlightProvider`, and later `IHotelProvider` and `IPaymentProvider`) in their modules, implemented by separate `Integrations.<Area>.<Supplier>` projects. ADR 0002 makes module types `internal` by default, and the architecture tests enforce it. An adapter in another assembly cannot implement an internal interface, and the port's signature types must be visible to it too. This was raised as a pending ARCHITECTURE REVIEW after Phase 1 and must be settled before the first port (Flights, Q3) ships.

## Decision

- Each port, and every type in its signatures, lives in a **public `TravelBooking.Modules.<Area>.Ports` namespace** inside `Modules.<Area>`. Everything else in the module stays `internal`.
- Port types are supplier-agnostic domain shapes: no ASP.NET Core, EF Core, or HTTP dependencies.
- **Only `Integrations.<Area>.*` adapters and the hosts reference `Modules.<Area>` for its port.** Other modules still use only `Modules.<Area>.Contracts`. An adapter references only the framework, BuildingBlocks, and its own area's module, never another module, adapter, or host.
- The shared provider error taxonomy (ADR 0004) and the `Money`/`Result` types that port signatures use live in `BuildingBlocks`.
- `ProviderOfferRef.Value` is an **opaque, adapter-owned token**. It may be long and carry whatever the supplier needs to revalidate or book the offer (for example, a full priced offer or search-time passenger ids). The core persists it verbatim with the offer snapshot and never parses it. Port types grow additively story by story (price breakdown, fare conditions, baggage, operating carrier).
- The port is **not frozen** until it has been validated against two real supplier shapes (ADR 0004; gated by Q6).
- The architecture tests enforce all of the above.

## Consequences

**Positive:** no extra project per module; adapters cannot reach module internals; the port surface is explicit and small.
**Negative:** a host or adapter could in theory use other public `Ports` types from a module. That is acceptable, because hosts compose modules anyway. If adapters ever need less than the whole module assembly, a separate `Modules.<Area>.Ports` project can supersede this ADR.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Separate `Modules.<Area>.Ports` project | One more project per module for no enforcement gain; the tests already restrict who references the module |
| `InternalsVisibleTo` per adapter | Brittle, and exposes every internal type to every adapter |
| Ports in `Modules.<Area>.Contracts` | Would expose supplier-facing ports to every other module |
