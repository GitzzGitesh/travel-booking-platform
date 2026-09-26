# 0018. Candidate flight supplier adapters and the provider capability model

- **Status:** Proposed (2026-09-26), implemented as directed for the Q6 preparation batch; acceptance is the project owner's. **This is not the Q6 supplier choice.** Each supplier still needs its own ADR (commercial model, merchant model, idempotency, lookup by reference, sandbox) before it can run in Production (ADR 0004).
- **Date:** 2026-09-26
- **Deciders:** Project owner (Q6 preparation batch)
- **Related:** [0004](0004-supplier-provider-abstraction.md), [0014](0014-provider-port-placement-and-visibility.md), ADR 0016 (hosting) and ADR 0017 (charge currency and FX), recorded on the docs branch for Q2/Q5, `docs/architecture/flight-suppliers.md`, `docs/runbooks/flight-supplier-onboarding.md`

## Context

Q6 (the first real flight supplier and its commercial model) is open. Amadeus, Sabre, Travelport and Duffel are all candidates, and we have no verified credentials or commercial approval for any of them. We want each to be addable behind the existing supplier-neutral port without a rewrite, and the application must know what each configured provider actually supports, without assuming it.

## Decision

- **One adapter project per candidate**: `Integrations.Flights.Amadeus`, `.Sabre`, `.Travelport` and `.Duffel`, beside the mock.
  - Each keeps its DTOs (internal, in a `Dtos` namespace), request and response mapping, error mapping, authentication and HTTP client inside the project.
  - Architecture tests enforce that DTOs are internal and that no module depends on an adapter.
- **A capability model in the Flights ports** (`FlightProviderCapabilities`). It contains:
  - a declaration per capability: Supported, Unsupported or RequiresConfirmation (the default, never assumed);
  - the adapter stage: Mock, Scaffolded, MappedFromDocumentation, SandboxVerified or ProductionReady;
  - the port operations the adapter implements.
  - Declarations start in code and are revised by configuration (`Integrations:Flights:<Name>:Capabilities:<Capability>`) after verification.
- **What the runtime acts on:** only the stage and the implemented operations. The capability declarations are the verified-readiness record (and the source of the matrix); they are informational and gate nothing. A provider that declares nothing implements nothing (fail closed).
- **The core respects capabilities:**
  - it never calls an operation the adapter does not implement;
  - checkout refuses an offer whose provider cannot book, before any payment is authorized;
  - it never routes an offer, booking or lookup to a provider other than the one named in `ProviderOfferRef.ProviderId`;
  - startup refuses a search provider (`Flights:SearchProviderId`) that does not implement search;
  - startup refuses any adapter below ProductionReady outside Development and Staging.
- **Composition by configuration:** an adapter is composed only when `Integrations:Flights:<Name>:Enabled` is true, with its credentials from user-secrets or Key Vault (validated at startup). Search goes to one provider; multi-provider search (fan-out) is not built.
- **Shared, supplier-neutral HTTP plumbing** in BuildingBlocks (`SupplierHttp`, `AccessTokenCache`):
  - per-operation timeouts;
  - status classification into the shared error taxonomy, where a failed write is Unknown and never retried;
  - token caching.
  - The read-only retry and circuit-breaker pipeline (`Microsoft.Extensions.Http.Resilience`, approved in ADR 0003) is a marked hook, added before any adapter goes to production.
- **Starting stages:**

  | Adapter | Stage | Implemented |
  |---|---|---|
  | Duffel | MappedFromDocumentation | search, revalidation |
  | Amadeus (Self-Service) | MappedFromDocumentation | search, revalidation |
  | Sabre | Scaffolded | no operations |
  | Travelport | Scaffolded | no operations |

  Booking is implemented for none of them. Their DTOs come from public documentation and are tested only against documentation-shaped fixtures; credential-gated sandbox contract tests exist and skip without credentials.

## Consequences

**Positive**
- A real supplier becomes an adapter plus mapping, configuration and registration. The core, Orders, Payments and the database need no change for it.
- Unsupported or unverified supplier behaviour can never be called by accident.

**Negative / trade-offs**
- Four adapter projects exist before any supplier is chosen; two are scaffolds.
- Documentation-derived mappings may be wrong until verified in a sandbox.
- The optional `Capabilities` member on the port adds surface to keep honest.

**Follow-ups**
- Per supplier: sandbox credentials, then the contract suite against the sandbox (SandboxVerified), then booking, lookup-by-reference and ticketing mapping, then a supplier ADR and commercial approval (ProductionReady).
- Add the resilience package (ADR 0003) at the marked hook.
- Store the provider id on the Orders item when booking is orchestrated.
- Allow an unstated cabin-bag allowance (some suppliers state only checked bags).
- Add market and charge-currency context to search requests (ADR 0017).
