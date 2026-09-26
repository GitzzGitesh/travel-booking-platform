# 0015. Synchronous cross-module commands for checkout orchestration

- **Status:** Accepted
- **Date:** 2026-09-26
- **Deciders:** Project owner (on the architecture review of PR #26)
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md) (refines its inter-module communication rule), [0004](0004-supplier-provider-abstraction.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0007](0007-async-processing-worker-and-outbox.md), `docs/architecture/booking-lifecycle.md`, `docs/architecture/payment-lifecycle.md`

## Context

ADR 0002 says modules call each other synchronously through Contracts **for queries**, and use **integration events via the outbox** for side effects. The checkout (ADR 0005: revalidate → authorize → book → capture) needs two calls that have side effects and whose result the caller needs before it can go on:

- `IFlightSelections.RevalidateAsync` (Flights): asks the supplier for the current price and saves a quote when the price changed.
- `IOrderPayments.AuthorizeAsync` / `ResumeAsync` (Payments): place or look up a payment hold, and save the payment attempt.

Orders cannot move an order to `Booking` without the revalidated price and the authorization outcome, and the customer is waiting for the answer (a price change to accept, an SCA challenge, a decline). An outbox event is dispatched later and gives no answer to the request. It would also turn one request into a multi-step saga with polling, for no safety gain: both calls are already idempotent, and their unknown outcomes are already recorded and looked up, never resubmitted. The architecture review of PR #26 flagged the conflict with ADR 0002's wording.

## Decision

We will allow **synchronous cross-module commands through Contracts** only when all of the following hold:

1. **The caller needs the result immediately** to continue the same request's workflow. The case this ADR covers is checkout orchestration: revalidation before payment, and payment authorization and its lookup.
2. **They are idempotent.** A caller-supplied key or a stable reference identifies the effect, and a database unique constraint enforces it (booking rules). Repeating the call never creates a second effect.
3. **They are supplier-neutral.** The contract carries only our own types. No supplier or payment-provider types, error codes or tokens cross it (ADR 0004).
4. **Their failures and unknown outcomes are explicit.** The contract returns a result type with distinct outcomes: success, definitive failures, and a pending or unknown state. The callee records an unknown outcome and resolves it by a lookup with our reference, never by resubmitting the write. The caller never proceeds on an unknown outcome.
5. **The caller owns the business checks it is trusted for.** For example, Orders checks that the customer owns the order before it calls Payments. An architecture test limits which modules may call such a command.

Everything else is unchanged:

- Durable asynchronous side effects and background work still use the **transactional outbox and the Worker** (ADR 0002, ADR 0007). This includes notifications, fulfilment, reconciliation jobs, expiry jobs, releasing unused payment holds, and reacting to another module's state changes.
- Synchronous calls for queries through Contracts stay as ADR 0002 describes.
- This is **not** a general permission for cross-module commands. A new synchronous command outside checkout orchestration needs its own ADR, or an amendment to this one, that shows the caller needs the immediate result.
- Each call commits in its own module's database transaction. There are no cross-module transactions. The caller copes with a partial outcome, for example a hold that exists while its order does not move to `Booking`, through the recorded state and the Worker.

## Consequences

**Positive**
- Checkout answers the customer in one request (price change, challenge, decline, or booking started), as ADR 0005 and the booking lifecycle describe.
- The existing implementation (PR #26) complies without redesign.

**Negative / trade-offs**
- The caller and the callee are coupled at run time: if Payments or the supplier is slow, checkout is slow. This is acceptable because checkout cannot finish without them anyway, and the supplier and provider calls have their own timeouts.
- Without a shared transaction, partial outcomes are possible. They are handled by recorded state, idempotent repeats and background reconciliation, not by rollback.

**Follow-ups**
- Releasing unused holds and reconciling open payment attempts use the Worker (the gates in `docs/progress.md`).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Route revalidation and authorization through the outbox | The customer needs the answer in the same request, so this becomes a saga with polling and client-visible intermediate states, for no safety gain over idempotent calls with explicit unknown states |
| Merge Payments into Orders | It weakens the ADR 0004 payment port boundary and Payments' own schema and state machine, and it doesn't help revalidation in Flights |
| Allow synchronous cross-module commands generally | This invites the coupling ADR 0002 exists to prevent. Each new case must justify itself |
