# 0030. Hotels: a second product on the Flights platform

- **Status:** Accepted (2026-10-05) under the delegated decision authority (the project owner handed Hotels, Q1, to Claude to decide as for a production application)
- **Date:** 2026-10-05
- **Deciders:** Engineering lead (delegated)
- **Related:** [0002](0002-modular-monolith.md), [0004](0004-supplier-provider-abstraction.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0006](0006-payments-stripe-manual-capture.md), [0010](0010-money-currency-and-time.md), [0027](0027-refunds-and-cancellations.md), [0029](0029-customer-trips-and-cancellation-requests.md), `docs/requirements/open-questions.md` (Q1, Q6, Q7)

## Context

The platform sells flights end to end: search, selection, price check, order, payment (authorize, book, capture), cancellation requests and refund cases. Hotels are the second product. The open business questions were:
- the commercial model (Q1: merchant or agency);
- the hotel supplier (Q6 for hotels).

The owner delegated the first; the supplier, its contract and its credentials are external.

## Decision

1. **Commercial model: merchant of record, prepaid rates only.**
   - We sell the rates the customer pays us for now, through the same payment flow as flights: authorize, then book, then capture on confirmation, and void on failure.
   - Pay-at-property rates are **not sold** at launch. They would add a second payment model (card guarantee, no-show rules) for little launch value.
   - Taxes and fees we collect are in the price. Fees the property collects itself (local taxes) are shown separately and are never charged by us.
2. **A Hotels module, mirroring Flights:**
   - `Modules.Hotels`, with its own `hotels` schema, and `Modules.Hotels.Contracts`, which Orders will use for booking (H2).
   - `IHotelProvider` is the supplier port (ADR 0004), with `Integrations.Hotels.Mock` as a deterministic, scenario-driven provider first.
   - **No real hotel supplier is assumed:** the bed bank or aggregator, its contract and its capabilities are external (Q6). Each later adapter must pass the same contract suite.
3. **Search:**
   - The destination is an IATA city code (as flights use airports).
   - Check-in and check-out are local dates (hotel dates are local by nature).
   - **One room per booking at launch:** 1–4 adults and 0–3 children (ages 0–17), up to 30 nights, at most 365 days ahead. Multi-room bookings come later.
   - Search is anonymous and rate limited, like flights.
4. **The offer:**
   - **Property:** the supplier-scoped id, name, address, city, country, star rating, and IANA time zone.
   - **Room:** its description and board basis (room only, breakfast, half board, full board, all inclusive).
   - **Price:** the **total for the stay, payable now** (Money); optionally the fees payable at the property, as information only.
   - **Cancellation policy:** non-refundable, or refundable until a deadline (an instant, shown in the property's local time) with the penalty that applies after it.
   - **Offer expiry.** The supplier's offer token is opaque and never leaves the module.
5. **Selection and price check:** the same discipline as flights:
   - a snapshot of the one selected offer, with unguessable ids;
   - an owner (signed-in) or anonymous selection; only an owned selection can be booked (Q8);
   - revalidation with the supplier before booking;
   - a changed price becomes a quote the customer must accept (F-01); expired or sold out ends the selection (F-02, F-03);
   - nothing the customer agreed to changes silently (F-53): a changed room, board or cancellation terms, even at the same price, is also a quote (marked `termsChanged`, with the terms to accept), and another property on revalidation ends the selection as sold out;
   - an offer we cannot store or show as stated (an over-long supplier id, a bad city or country code, star rating or time zone) is dropped at search and refused at revalidation, never truncated (F-54).

   The quote logic is copied from Flights rather than shared. Two products do not yet justify a shared abstraction, so it is extracted if a third product arrives.
6. **Booking, in a later batch (H2):**
   - Order items gain a product (flight or hotel) and keep one state machine.
   - Orders books a hotel through `Modules.Hotels.Contracts`, under our order item id as the client reference.
   - An unknown outcome goes to `PendingConfirmation`, and reconciliation looks the booking up by our reference; nothing is ever resubmitted.
   - Capture happens only after the supplier confirms.
7. **Cancellation, in a later batch (H4):**
   - Customers ask (ADR 0029), staff decide (ADR 0027, maker-checker).
   - The refund follows the booked rate's policy as stored at booking: in full before the deadline, the total less the penalty after it, nothing for a non-refundable rate, less any disclosed fee.
   - Supplier cancellation through the adapter happens only when its capability is verified; until then it is done at the supplier's desk.
   - Consumer rights per market: pending legal confirmation.

## Consequences

- Hotels reuse identity, orders, payments, refunds, notifications and operations. Only search, offers, selection and the supplier port are new.
- One payment model keeps money safety rules identical across products.
- Not selling pay-at-property rates narrows the inventory a supplier offers us; this is a commercial trade-off to revisit with the supplier (Q6).
- Single-room bookings exclude families needing two rooms at launch; they book twice.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Agency model (the hotel charges) | Card details would pass to suppliers (PCI scope beyond SAQ-A), and there would be two refund models |
| A generic "product" module for flights and hotels | Premature: the products differ in search, offers and fulfilment; shared rules live in Orders and Payments |
| Multi-room from the start | More supplier variance and partial-booking states for little launch value |
