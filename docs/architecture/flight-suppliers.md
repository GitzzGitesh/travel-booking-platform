# Flight suppliers: readiness and capabilities (Q6)

**Status: preparation.** No supplier is chosen, credentialed, commercially approved or production-ready. This page records what is **technically prepared** and what still needs **credentials**, **supplier confirmation** or **commercial verification**. Anything not verified is marked as such, never assumed. Structure and rules: ADR 0018.

## Readiness levels
| Level | Meaning | Duffel | Amadeus | Sabre | Travelport |
|---|---|---|---|---|---|
| Technically prepared | Adapter project, configuration, credential contract, capabilities, error mapping | ✔ | ✔ | ✔ | ✔ |
| Mapped (documentation) | Search and revalidation mapped from public docs, fixture-tested | ✔ | ✔ | ✘ | ✘ |
| Sandbox / credential ready | Contract suite passes against the supplier's sandbox | ✘ (needs a test token) | ✘ (needs test API keys) | ✘ | ✘ |
| Supplier confirmation | Open items below answered | ✘ | ✘ | ✘ | ✘ |
| Commercial verification | Agreement, content and markets, ticketing and funding model | ✘ | ✘ | ✘ | ✘ |
| Production-ready | Supplier ADR, all of the above; the only stage allowed outside Development and Staging | ✘ | ✘ | ✘ | ✘ |

## Capability matrix (as declared in code)
Legend: **S** supported (documented and relied on by our mapping), **U** unsupported, **?** requires confirmation.

| Capability | Duffel | Amadeus (Self-Service) | Sabre | Travelport | Mock |
|---|---|---|---|---|---|
| Search / one-way / round-trip | S | S | ? | ? | S |
| Multi-city | S (not sent by our search yet) | S (not sent yet) | ? | ? | U |
| Passenger types | S (confirm children without ages) | S | ? | ? | S |
| Cabin selection | S | S | ? | ? | S |
| Baggage | S | ? (checked bags only) | ? | ? | S |
| Fare conditions | S | ? (separate fare-rules call) | ? | ? | S |
| Branded fares | ? | ? | ? | ? | U |
| Revalidation | S | S | ? | ? | S |
| Booking | ? | ? (consolidator for ticketing) | ? | ? | S |
| Lookup by our reference | ? | ? (not documented) | ? | ? | S |
| Lookup by supplier reference | ? | ? | ? | ? | U |
| Idempotent booking by our reference | ? | ? | ? | ? | S |
| Cancellation / refunds / exchanges | ? | ? | ? | ? | U |
| Schedule-change notifications | ? | ? | ? | ? | U |
| Ticketing | ? | ? | ? | ? | U |
| Ancillaries / seat selection | ? | ? | ? | ? | U |
| Requested currency | ? | ? | ? | ? | U (XTS only) |
| Market coverage | ? (commercial) | ? (commercial) | ? | ? | S (synthetic) |

The declarations are the readiness record; the runtime acts only on the stage and the implemented operations (a provider that declares nothing implements nothing). Revise a declaration after verification with configuration, without a code change: `Integrations:Flights:<Name>:Capabilities:<Capability> = Supported | Unsupported | RequiresConfirmation`.

## Per supplier

### Duffel
- **Configuration:** `Integrations:Flights:Duffel`: `Enabled`, `BaseUrl` (Duffel's API host), `AccessToken` (secret; test and live tokens are separate), `ApiVersion` (sent as the `Duffel-Version` header).
- **Authentication:** bearer access token.
- **Mapped:**
  - search is an offer request; revalidation retrieves the offer again;
  - offers carry their own expiry;
  - segments carry the operating carrier, duration and fare basis;
  - conditions give refund and change allowances with penalties;
  - baggage is taken from included bags per segment.
- **Not claimed:**
  - a per-passenger-type price breakdown (Duffel states offer totals);
  - the validating carrier is taken from the offer owner, to confirm.
- **Requires supplier confirmation:**
  - responses for expired and sold-out offers (today: the shared status mapping);
  - child passengers without ages;
  - order creation with payment from a balance under our merchant-of-record model;
  - whether orders can be found by our reference, and idempotency of order creation;
  - ticketing timing;
  - cancellations, refunds and changes;
  - schedule-change events;
  - the offer currency rules;
  - market coverage.

### Amadeus (Self-Service APIs)
- **Configuration:** `Integrations:Flights:Amadeus`: `Enabled`, `BaseUrl` (test or production environment), `ClientId` and `ClientSecret` (secrets), `MaxOffers`, `OfferLifetime`.
- **Authentication:** OAuth2 client credentials; the token is cached and refreshed. A rejected token is replaced once.
- **Mapped:**
  - search is Flight Offers Search, sent as a POST with a method-override header;
  - revalidation is Flight Offers Price, which receives the offer back whole, so the offer's JSON is its opaque reference;
  - a per-traveler price breakdown, the validating carrier, and the operating carrier, duration and fare basis per segment;
  - the last ticketing date, read as the start of that day in UTC.
- **Our policy, to confirm:** Amadeus states no offer expiry in the mapped fields, so offers are treated as valid for `OfferLifetime` (15 minutes by default).
- **Not claimed:**
  - baggage (only checked bags are stated);
  - fare conditions (a separate fare-rules call).
- **Requires supplier or commercial confirmation:**
  - production ticketing (a consolidator agreement);
  - lookup by our reference (not documented): otherwise the adapter must keep the Amadeus order id before returning, which needs an ADR;
  - idempotency of order creation;
  - cancellation and refunds;
  - currency parameter behaviour;
  - content and markets.

### Sabre (scaffolded)
- **Configuration:** `Integrations:Flights:Sabre`: `Enabled`, `BaseUrl`, `ClientId`, `ClientSecret` (secrets) and `PseudoCityCode`.
- **Status:** no operation is implemented. An enabled Sabre adapter cannot be chosen for search and is never called.
- **Requires Sabre agency access and documentation:**
  - the token scheme, and the shopping, revalidation, booking (PNR), lookup and ticketing APIs and schemas;
  - whether bookings can be found by our reference, or the PNR locator must be kept (ADR);
  - idempotency;
  - accreditation for ticketing (BSP/ARC or a consolidator);
  - content and markets.

### Travelport (scaffolded)
- **Configuration:** `Integrations:Flights:Travelport`: `Enabled`, `BaseUrl`, `ClientId`, `ClientSecret`, `Username`, `Password` (secrets) and `AccessGroup`.
- **Status:** no operation is implemented; as for Sabre.
- **Requires Travelport access and documentation:**
  - the token scheme and which API generation (JSON or the older XML API);
  - search, pricing, booking, lookup and ticketing schemas;
  - reference and idempotency behaviour;
  - accreditation;
  - content and markets.

## Currency (Q5 policy)
An offer's price is in the **supplier's currency**. The customer's charge currency is a separate, later pricing step (the FX policy), and adapters never convert. Whether a supplier can be asked for a particular currency is the `RequestedCurrency` capability, which requires confirmation for all four.
