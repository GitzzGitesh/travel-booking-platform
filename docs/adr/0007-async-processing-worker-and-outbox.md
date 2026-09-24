# 0007. Async processing: Worker host and SQL transactional outbox

- **Status:** Accepted
- **Date:** 2026-09-24
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0006](0006-payments-stripe-manual-capture.md)

## Context

Several flows must happen reliably outside the HTTP request: webhook processing, reconciliation of unknown booking/payment states, fulfilment (tickets/vouchers), notifications, offer and authorization expiry, and inter-module integration events. Publishing an event after a DB commit can lose it if the process crashes. Introducing a message broker adds infrastructure, cost, and failure modes.

## Decision

- Two hosts from one codebase: **`Api`** (HTTP only; webhook endpoints just verify and persist) and **`Worker`** (background processing).
- **Transactional outbox**: modules write integration events to an outbox table **in the same transaction** as their state change. The Worker dispatches them to in-process handlers with at-least-once semantics.
- **Inbox**: incoming external events (webhooks) and consumed integration events are recorded with a unique key for deduplication. All handlers are idempotent.
- Scheduled jobs (reconciliation, expiry) run in the Worker using a simple hosted-service scheduler with a DB lease/lock, so multiple Worker instances don't double-process.
- **No message broker** (Service Bus, Kafka, RabbitMQ) and no MassTransit/NServiceBus. Revisit with a new ADR only when measured load, fan-out, or cross-service needs justify it.
- Trace context is stored with outbox messages so traces continue across async hops.

## Consequences

**Positive:** reliable side effects with no new infrastructure; a single database to back up; simple local development.
**Negative:** SQL polling latency (seconds). We must build the outbox/inbox/lease plumbing (small, in BuildingBlocks). DB load grows with volume (monitor it).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Azure Service Bus from day one | Extra infrastructure and cost; still needs an outbox for atomicity |
| Background work in the Api process | Couples scaling and deployments; long jobs affect request latency |
| Hangfire / Quartz | Viable later if scheduling needs grow; not needed for the initial jobs |
