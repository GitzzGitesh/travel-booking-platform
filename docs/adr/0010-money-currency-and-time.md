# 0010. Money, currency, and time conventions

- **Status:** Proposed. Rounding and FX policy details depend on open question Q5.
- **Date:** 2026-09-24
- **Related:** `.claude/rules/booking-and-payments.md`, `.claude/rules/database.md`, `.claude/rules/api-design.md`

## Context

Money and time bugs in travel are common and expensive: float rounding, mixed currencies, currencies with 0 or 3 decimals, flights shown in the wrong time zone, hotel cancellation deadlines misread across zones, DST transitions.

## Decision

**Money**
- A `Money` value object: `decimal Amount` + `Currency` (ISO-4217 code with minor-unit metadata). Arithmetic across different currencies throws.
- Never `double`/`float` for money, in any layer.
- DB: `decimal(19,4)` + `char(3)`. JSON: `{ "amount": "123.45", "currency": "EUR" }` (string amount).
- Rounding: intermediate calculations keep full precision. Round **once**, when producing a customer-facing or provider-facing amount, to the currency's minor units using **MidpointRounding.AwayFromZero** (commercial rounding), unless a supplier or legal rule specifies otherwise.
- Conversion to provider minor units (e.g. Stripe integer amounts) happens only in adapters.
- FX: any conversion records the rate, source, and timestamp on the price breakdown. The FX policy (who carries risk, which rate source) is pending Q5.
- Price breakdowns (base fare/rate, taxes, fees, markup, discount, pay-at-property) are stored as a snapshot on the order item.

**Time**
- Instants (created, paid, expiry, deadlines as absolute moments): `DateTimeOffset` in UTC; DB `datetimeoffset`.
- Flight departure/arrival: **local date-time + airport code** (zone derived from airport data) as provided by the supplier. Never converted for storage.
- Hotel check-in/out: `DateOnly` local dates.
- Supplier cancellation deadlines: stored exactly as supplied (local time + zone, or instant) and displayed with the zone.
- Code uses the injected `TimeProvider`. Tests use `FakeTimeProvider`.
- Passenger age rules are evaluated at the **travel date**.

## Consequences

**Positive:** eliminates whole classes of rounding and zone bugs; auditable prices.
**Negative:** more types and some verbosity; airport time-zone reference data must be maintained.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Store amounts as integer minor units everywhere | Awkward for multi-step pricing with percentages; minor units vary per currency. We use it only at the provider edge |
| Banker's rounding | Not what customers or most payment providers expect for displayed prices |
| Store all times in UTC | Loses the supplier's local semantics; conversions around DST cause off-by-one-hour errors |
