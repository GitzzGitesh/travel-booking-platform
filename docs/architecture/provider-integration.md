# Provider integration

**Status: Draft.** Proposed in ADR 0004. Port signatures are illustrative and will be finalised in Phase 2.

## Structure

```
Modules.Flights          → defines IFlightProvider (port) + domain types
Integrations.Flights.Mock    → implements IFlightProvider (deterministic, scenario-driven)
Integrations.Flights.<Supplier> → implements IFlightProvider (maps supplier DTOs ↔ domain)
```

The same pattern applies to `IHotelProvider` (Modules.Hotels) and `IPaymentProvider` (Modules.Payments). The core never references an `Integrations.*` project. The hosts wire them up via DI and configuration.

## Port shape (illustrative)

```csharp
public interface IFlightProvider
{
    ProviderId Id { get; }
    Task<Result<FlightSearchResult>> SearchAsync(FlightSearchRequest request, CancellationToken ct);
    Task<Result<FlightOffer>> RevalidateAsync(ProviderOfferRef offer, CancellationToken ct);
    Task<Result<ProviderBookingResult>> BookAsync(FlightBookingRequest request, CancellationToken ct); // request carries our ClientReference
    Task<Result<ProviderBookingStatus>> RetrieveAsync(ClientReference reference, CancellationToken ct);   // used by reconciliation
    Task<Result<ProviderCancellationQuote>> QuoteCancellationAsync(ProviderBookingRef booking, CancellationToken ct);
    Task<Result<ProviderCancellationResult>> CancelAsync(ProviderBookingRef booking, CancellationToken ct);
}
```

Requirements:
- **`RetrieveAsync` by our client reference is mandatory.** Reconciliation depends on it. If a supplier cannot look up by client reference, the adapter must persist the supplier reference before returning, and that limitation needs an ADR.
- Every operation declares whether it is **idempotent** at the provider. The resilience pipeline is chosen from that flag.
- Results use a shared **error taxonomy**, never supplier error codes.

## Error taxonomy
| Error | Meaning | Core behaviour |
|---|---|---|
| `PriceChanged` | Revalidated price differs | 422 to customer, ask to accept |
| `OfferExpired` / `SoldOut` | Offer no longer bookable | Re-search |
| `InvalidRequest` | Supplier rejected our data (names, documents) | Show a validation error; fix mapping if systemic |
| `Rejected` | Definitive booking failure | `Booking → Failed`, void payment |
| `Unknown` | Timeout, connection reset, ambiguous 5xx **on a write** | `PendingConfirmation`, reconcile |
| `Unavailable` | Supplier down / circuit open | Degrade; no booking attempt |
| `RateLimited` | Throttled | Back off (reads only) |
| `AuthFailure` | Credentials invalid | Alert immediately; treat as `Unavailable` |
| `IdempotencyConflict` | Our idempotency key was reused with different details | Definitive: nothing applied |
| `OperationInProgress` | A request with the same key is still being processed | Unknown: look it up later; never a refusal |

## Timeouts and resilience
| Operation class | Timeout (initial, tune later) | Retry | Circuit breaker |
|---|---|---|---|
| Search | ~10–20 s, per supplier | 1 retry on transient | Yes |
| Revalidate / retrieve | ~10 s | Up to 2 on transient | Yes |
| Book / ticket / cancel | ~60 s (supplier-dependent) | **None** | Yes (fail fast when open) |

## Mock providers
Deterministic and scenario-driven, used for local dev, automated tests, and demos. They are available **only in non-production environments** (registration guarded by environment and configuration).

Implemented so far (flights): `SearchAsync`, `RevalidateAsync`, `BookAsync` (with our `ClientReference` as the idempotency token) and `RetrieveBookingAsync` (a lookup by our reference: "not found" is a definite answer, an error means still unknown). Booking scenarios are chosen by a reserved passenger family name (`MockBookingScenarios`): `SCENARIO-REJECTED`, `SCENARIO-TIMEOUT-BOOKED` (the call is `Unknown` but the lookup finds the booking) and `SCENARIO-TIMEOUT-NOT-BOOKED`. Timeouts are simulated instantly. Mock bookings are held in memory for the life of the process, so a restart or a second instance (e.g. in Staging) makes a lookup answer "not found" for bookings it made. Do not draw reconciliation conclusions from the mock across restarts.

**On a booking write** the core treats only `Rejected`, `PriceChanged`, `SoldOut`, `OfferExpired` and `InvalidRequest` as "definitely not booked". `Unknown`, `Unavailable`, `RateLimited`, `AuthFailure` and any exception from a started call are an unknown outcome, settled by a lookup and never resubmitted. A booking that exists but not as agreed (price, reference or provider) is a mismatch for manual review, never booked.

**Payment mock** (`Integrations.Payments.Mock`, provider id `mockpay`): scenarios are chosen by test payment-method tokens (`MockPaymentMethods`):
- `pm_mock_approved`;
- `pm_mock_declined` and `pm_mock_insufficient_funds` (a `Declined` payment state);
- `pm_mock_requires_action` (an SCA challenge that is never completed);
- `pm_mock_timeout_authorized` and `pm_mock_timeout_not_authorized`;
- `pm_mock_capture_timeout` (captured, answer lost; a same-key retry returns it);
- `pm_mock_refund_unavailable_once`;
- `pm_mock_refund_pending` (refunds accepted as `Pending`).

The configured scenario `Unavailable` refuses every call. Payments live in memory, so a restart or a second instance makes a lookup answer "not found" for a real hold. Every payment adapter must pass `PaymentProviderContract` using its provider's own test methods, never real cards.

**Adding a real flight supplier:** its ADR must state (1) how it guarantees at most one booking per client reference (supplier idempotency, or a lookup before booking); (2) the lookup's consistency window after a write, which reconciliation must wait out before treating "not found" as not booked; (3) whether a late request with the same client reference can still create a booking; and (4) how sandbox bookings made by the contract suite are cancelled. `RevalidateAsync` returns the offer as the supplier prices it now, and the core compares prices; it does not return a `PriceChanged` error. Search scenarios are chosen by configuration (`Integrations:Flights:Mock:Scenario`). Revalidation scenarios are chosen per offer by reserved test destinations (`MockRevalidationScenarios`), so one running Api can demonstrate all of them: `ZPC` price changed (F-01, +15%), `ZEX` offer expired (F-02), `ZSO` sold out (F-03). The reference returned by revalidation replaces the stored one, because a supplier may issue a new priced offer that must then be booked. **Known gap:** the core does not yet check that the revalidated itinerary is unchanged (only the contract suite asserts it). Handle schedule changes at pricing time with the first real supplier and with `BookAsync`.

Scenarios (selected via a test header/config or magic values, e.g. passenger surname `SCENARIO-TIMEOUT`):
`success` · `price-changed` · `offer-expired` · `sold-out` · `rejected` · `timeout-then-booked` (book times out, retrieve later finds it) · `timeout-not-booked` · `unavailable` · `slow` · `ticketing-failed` · `cancel-unknown` · `schedule-change` (later).

Every mock must pass the same **provider contract suite** as the real adapters (mock parity).

## Observability and records
- Each supplier call gets a span with provider ID, operation, client reference, outcome category, and latency.
- Request/response payloads are captured **after redaction** (PII, card data) to a supplier-call log with a retention limit (TBD), for dispute evidence and debugging.
- Metrics: calls, errors by taxonomy category, latency per provider and operation, look-to-book ratio.

## Flight model as built (supplier-neutral)
- **Offer facts** (`FlightOffer.Fare`, all optional: an unstated fact is unknown, never guessed):
  - a price breakdown by passenger type (base fare, and taxes and fees as one amount, per passenger; a type may have several entries, e.g. children priced by age). One that does not add up to the offer's total is dropped and logged, and the total stays the price;
  - the validating carrier;
  - baggage per passenger (checked bags, optional weight limit per bag, cabin bags);
  - refund and change conditions (`NotStated`, `NotAllowed`, `AllowedWithFee`, `Free`; penalty amounts come with fare rules);
  - the ticketing deadline (an instant).
- **Leg facts** (`FlightSegment`): the operating carrier when it is not the marketing carrier (codeshare), the flying time if the supplier states it, and the fare basis (opaque to the core).
- **Airports** (`IAirportDirectory`, core reference data inside Flights and not part of the provider port, ADR 0014): name, city, country and IANA time zone, from a small embedded seed dataset (`Infrastructure/ReferenceData/airports.json`, validated at startup). It is to be replaced by an authoritative source before production.
  - Local flight times stay local (ADR 0010). The API gives each leg's time zones when known.
  - The flying time is the supplier's, or else computed from both airports' zones.
  - An airport outside the dataset is shown by its code.
  - The mock states its flying time but adds it on the departure clock (a mock simplification).
- **Providers by id** (`FlightProviders`): revalidation, booking and booking lookup go to the provider that made the offer (`ProviderOfferRef.ProviderId`).
  - An id that is not composed is never sent to another provider: revalidation reports it unavailable, booking refuses it unsent, and a lookup stays unknown.
  - Search uses `Flights:SearchProviderId` when several providers are composed, otherwise the only one; searching several providers needs a fan-out design first.
- **Adding a real flight supplier** is then:
  - one `Integrations.Flights.<Supplier>` adapter that maps the supplier's DTOs to these types and its errors to the taxonomy;
  - its configuration and secrets;
  - one provider registration;
  - passing `FlightProviderSearchContract`, including its rule that stated fare facts are consistent with the search.

## Candidate suppliers (Q6)
Amadeus, Sabre, Travelport and Duffel have adapter projects behind the port, with a declared capability model, an adapter stage and credential-gated sandbox contract tests (ADR 0018). Readiness, the capability matrix and the open items per supplier are in [flight-suppliers.md](flight-suppliers.md); onboarding steps are in [the runbook](../runbooks/flight-supplier-onboarding.md).

## Adding a real supplier
Workflow skill `add-provider-adapter` (to be created in Phase 2). An ADR is required per supplier: commercial model, merchant model, idempotency support, retrieve-by-reference support, and sandbox availability.
