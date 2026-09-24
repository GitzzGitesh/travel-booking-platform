# 0011. Caching: HybridCache first, Redis deferred

- **Status:** Proposed
- **Date:** 2026-09-24
- **Related:** [0003](0003-baseline-technology-stack.md), [0005](0005-order-aggregate-and-booking-orchestration.md)

## Context

The initial direction included Redis. Caching will help with search results (supplier cost and latency), reference data (airports, currencies), and rate limiting. But Redis adds infrastructure, cost, and failure modes. Using a cache as the source of truth for offers or bookings would be dangerous (eviction means lost state).

## Decision

- Use **.NET `HybridCache`** from the start (in-memory L1, stampede protection).
- Add **Azure Cache for Redis as L2** when running more than one Api instance needs shared cache or distributed rate limiting (expected by first production deployment). It's a configuration change, not a code change.
- **The cache is never a source of truth.** Offers selected for checkout, order state, idempotency records, and payment state live in SQL.
- Search results are cached briefly (TTL tuned per supplier's offer expiry). The selected offer is always revalidated before booking.
- Cache keys never contain PII.

## Consequences

**Positive:** no new infrastructure early; a clear rule prevents cache-as-database mistakes.
**Negative:** single-instance cache semantics until Redis is added; rate limiting is per instance until then.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Redis from day one | Infrastructure before need; no benefit on a single instance |
| No caching | Supplier cost and latency on repeated searches; look-to-book limits |
