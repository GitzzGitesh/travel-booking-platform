# 0019. Amadeus as the first production flight supplier (Q6)

- **Status:** Accepted (2026-09-27) as a business decision. **The Amadeus adapter is not production-ready**: it stays at stage MappedFromDocumentation until the requirements below are met.
- **Date:** 2026-09-27
- **Deciders:** Project owner
- **Related:** [0004](0004-supplier-provider-abstraction.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0014](0014-provider-port-placement-and-visibility.md), [0018](0018-candidate-flight-supplier-adapters-and-capabilities.md), Q1 and Q6 in `docs/requirements/open-questions.md`, `docs/architecture/flight-suppliers.md`, `docs/runbooks/flight-supplier-onboarding.md`

## Context

Q6 asked which flight supplier comes first, and on what commercial model. PR #29 (ADR 0018) prepared four candidate adapters: Amadeus, Sabre, Travelport and Duffel. It also added a capability model that keeps an adapter out of Production until it is ProductionReady. None of the four is credentialed, sandbox-verified or under contract. Q1 made our company the merchant of record for flights.

## Decision

1. **Amadeus is the first production flight supplier** and the first real supplier integration. The existing adapter, `Integrations.Flights.Amadeus` (provider id `amadeus`), is continued. It is not replaced.
2. **Our company is the merchant of record** (Q1). We charge the customer through our payment provider, and we settle with Amadeus, or with the ticketing party Amadeus requires, separately.
3. **Sabre, Travelport and Duffel remain supported future integrations.** Their adapters stay as they are (ADR 0018). Adding one later means a new or completed adapter, its configuration and its own supplier ADR, with no change to core booking logic. Offers, selections and bookings are routed by `ProviderId`, and search goes to the provider set in `Flights:SearchProviderId`.
4. **Production readiness is earned, not declared.** This relies on the capability model and startup rule built in PR #29. ADR 0018, which describes them, is still Proposed, and this ADR does not accept it. The adapter's stage rises to SandboxVerified only when the sandbox contract suite passes with real test credentials. It becomes ProductionReady only when every requirement below is met. Startup already refuses anything below ProductionReady outside Development and Staging.
5. **Booking and lookup are deferred to a follow-up Amadeus booking ADR.** That ADR records:
   - how at most one booking per our reference is guaranteed;
   - how a booking is found by our reference, or whether the Amadeus order id must be persisted before the call returns (provider-integration.md);
   - the not-found consistency window;
   - how ticketing is done.

   It is written when the sandbox and the supplier's answers exist. Until then `BookAsync` and `RetrieveBookingAsync` send nothing, and checkout refuses Amadeus offers before payment (`SupplierCannotBook`).

## Remaining Amadeus requirements

| # | Requirement | Status | Needed from |
|---|---|---|---|
| R1 | **Amadeus product**: Self-Service APIs (what the adapter maps) or Enterprise (a different API family and contract) | Open | Business owner with Amadeus |
| R2 | **Test (sandbox) credentials**: API key and secret for the test environment | Missing | Business owner / Amadeus account |
| R3 | **Production credentials**, per environment, stored in Key Vault | Missing (and gated on R4) | Amadeus, after the agreement |
| R4 | **Commercial agreement / account**: production access, pricing, and who issues tickets (Self-Service production ticketing requires a consolidator agreement or our own accreditation) | Missing | Business owner |
| R5 | **Booking idempotency**: whether Flight Create Orders accepts our reference as an idempotency key; otherwise how duplicates are prevented | Unconfirmed | Amadeus, then sandbox proof |
| R6 | **Lookup and reconciliation**: lookup by our reference is not documented. Confirm an alternative, or persist the Amadeus order id before returning (booking ADR). Also the not-found consistency window | Unconfirmed | Amadeus, then sandbox proof |
| R7 | **Ticketing**: who issues tickets, when, how issuance is confirmed, and the deadline behaviour (`lastTicketingDate`) | Unconfirmed | Amadeus / consolidator |
| R8 | **Cancellation, void and refund**: availability through the API, the void window, and refund handling | Unconfirmed | Amadeus / consolidator |
| R9 | **Currency**: the settlement currency; whether `currencyCode` is honoured for every fare (Q5: the supplier currency is never relabelled as the charge currency) | Unconfirmed | Amadeus, then sandbox proof |
| R10 | **Market and route coverage**: content by market; low-cost and NDC content; the markets our agreement allows | Unconfirmed | Amadeus / agreement |
| R11 | **Rate limits and commercial constraints**: production transaction limits, quotas and look-to-book terms. The test environment has its own lower limits and a limited data set | Unconfirmed | Amadeus / agreement |
| R12 | **Offer validity**: how long a priced offer stays bookable (our `OfferLifetime` of 15 minutes is a placeholder policy) | Unconfirmed | Amadeus, then sandbox proof |
| R13 | **Error codes**: which Amadeus error codes mean sold out, expired or price changed. Codes are recorded now, but they do not change the error kind until confirmed | Unconfirmed | Sandbox |

## Consequences

- Q6 is answered, and the port can be validated against a real supplier shape (ADR 0004) once R2 exists.
- No live Amadeus call, booking or ticket has been made. Nothing in the repository claims otherwise.
- The follow-up Amadeus booking ADR is the next architecture step for this supplier, and it is gated on R2, R5 and R6.
- The other three adapters carry no cost beyond their tests, and they keep the port honest about differences between suppliers.
