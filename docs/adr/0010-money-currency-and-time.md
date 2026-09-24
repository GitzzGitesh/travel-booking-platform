# 0010. Money, currency, and time conventions

- **Status:** Accepted. Not blocked by open question Q5: the business FX policy is out of scope here (see **Out of scope**).
- **Date:** 2026-09-24 (revised 2026-09-25: separated the technical conventions from the later business FX policy)
- **Related:** `.claude/rules/booking-and-payments.md`, `.claude/rules/database.md`, `.claude/rules/api-design.md`

## Context

Money and time bugs in travel are common and expensive: float rounding, mixed currencies, currencies with 0 or 3 decimals, flights shown in the wrong time zone, hotel cancellation deadlines misread across zones, DST transitions.

## Decision

This ADR fixes technical conventions only. They hold whichever currencies we charge in and whoever carries FX risk.

**Money representation**
- A `Money` value object: `decimal Amount` + `Currency` (ISO-4217 code with minor-unit metadata).
- Never `double`/`float` for money, in any layer.
- DB: `decimal(19,4)` + `char(3)`. JSON: `{ "amount": "123.45", "currency": "EUR" }` (string amount).
- Rounding: intermediate calculations keep full precision. Round **once**, when producing a customer-facing or provider-facing amount, to the currency's minor units using **MidpointRounding.AwayFromZero** (commercial rounding), unless a supplier or legal rule specifies otherwise.
- Price breakdowns (base fare/rate, taxes, fees, markup, discount, pay-at-property) are stored as a snapshot on the order item.

**Currency handling**
- Every amount carries its currency. Arithmetic across different currencies throws.
- Minor units come from ISO-4217 metadata (e.g. JPY 0, EUR 2, KWD 3), never from an assumed 2 decimals.
- Conversion to provider minor units (e.g. Stripe integer amounts) happens only in adapters.
- Any currency conversion is an explicit operation that records the rate, its source, and the timestamp on the price breakdown. This is a mechanism; *which* conversions happen is business policy (see **Out of scope**).

**Time**
- Instants (created, paid, expiry, deadlines as absolute moments): `DateTimeOffset` in UTC; DB `datetimeoffset`.
- Flight departure/arrival: **local date-time + airport code** (zone derived from airport data) as provided by the supplier. Never converted for storage.
- Hotel check-in/out: `DateOnly` local dates.
- Supplier cancellation deadlines: stored exactly as supplied (local time + zone, or instant) and displayed with the zone.
- Code uses the injected `TimeProvider`. Tests use `FakeTimeProvider`.
- Passenger age rules are evaluated at the **travel date**.

**Out of scope: business FX policy**
The charge (presentment) currencies per market, who carries FX risk, the rate source and margin, and settlement currencies depend on open question Q5 (and on Q2 for markets). They will be recorded in a separate ADR once Q5 is answered, before real payment/currency integration. That ADR builds on the mechanisms above and does not change them.

## Consequences

**Positive:** eliminates whole classes of rounding and zone bugs; auditable prices.
**Negative:** more types and some verbosity; airport time-zone reference data must be maintained.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Store amounts as integer minor units everywhere | Awkward for multi-step pricing with percentages; minor units vary per currency. We use it only at the provider edge |
| Banker's rounding | Not what customers or most payment providers expect for displayed prices |
| Store all times in UTC | Loses the supplier's local semantics; conversions around DST cause off-by-one-hour errors |
