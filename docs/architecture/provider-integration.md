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

## Timeouts and resilience
| Operation class | Timeout (initial, tune later) | Retry | Circuit breaker |
|---|---|---|---|
| Search | ~10–20 s, per supplier | 1 retry on transient | Yes |
| Revalidate / retrieve | ~10 s | Up to 2 on transient | Yes |
| Book / ticket / cancel | ~60 s (supplier-dependent) | **None** | Yes (fail fast when open) |

## Mock providers
Deterministic and scenario-driven, used for local dev, automated tests, and demos. They are available **only in non-production environments** (registration guarded by environment and configuration).

Implemented so far (flights): `SearchAsync` and `RevalidateAsync`. `RevalidateAsync` returns the offer as the supplier prices it now, and the core compares prices; it does not return a `PriceChanged` error. Search scenarios are chosen by configuration (`Integrations:Flights:Mock:Scenario`). Revalidation scenarios are chosen per offer by reserved test destinations (`MockRevalidationScenarios`), so one running Api can demonstrate all of them: `ZPC` price changed (F-01, +15%), `ZEX` offer expired (F-02), `ZSO` sold out (F-03). The reference returned by revalidation replaces the stored one, because a supplier may issue a new priced offer that must then be booked. **Known gap:** the core does not yet check that the revalidated itinerary is unchanged (only the contract suite asserts it). Handle schedule changes at pricing time with the first real supplier and with `BookAsync`.

Scenarios (selected via a test header/config or magic values, e.g. passenger surname `SCENARIO-TIMEOUT`):
`success` · `price-changed` · `offer-expired` · `sold-out` · `rejected` · `timeout-then-booked` (book times out, retrieve later finds it) · `timeout-not-booked` · `unavailable` · `slow` · `ticketing-failed` · `cancel-unknown` · `schedule-change` (later).

Every mock must pass the same **provider contract suite** as the real adapters (mock parity).

## Observability and records
- Each supplier call gets a span with provider ID, operation, client reference, outcome category, and latency.
- Request/response payloads are captured **after redaction** (PII, card data) to a supplier-call log with a retention limit (TBD), for dispute evidence and debugging.
- Metrics: calls, errors by taxonomy category, latency per provider and operation, look-to-book ratio.

## Adding a real supplier
Workflow skill `add-provider-adapter` (to be created in Phase 2). An ADR is required per supplier: commercial model, merchant model, idempotency support, retrieve-by-reference support, and sandbox availability.
