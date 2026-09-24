---
name: travel-domain
description: Travel-industry domain reference for flights, hotels, the booking lifecycle, payments/refunds, and real-world failure modes. Use when designing, implementing, or reviewing anything involving search, offers, pricing, orders, bookings, PNRs, ticketing, hotel reservations, cancellations, refunds, payments, provider integrations, or reconciliation, or when a domain term is unclear.
---

# Travel domain reference

Background knowledge so that designs and code match how flights, hotels, and payments actually behave. **The canonical project decisions live in `docs/`.** This skill explains the domain; the docs define what we build.

## Load the reference you need

| Topic | File |
|---|---|
| Flights: offers, PNR, ticketing, fares, schedule changes, passengers | [flights.md](flights.md) |
| Hotels: rates, rooms, cancellation policies, bed banks, vouchers | [hotels.md](hotels.md) |
| Booking lifecycle: order/item flow, unknown outcomes, reconciliation | [booking-lifecycle.md](booking-lifecycle.md) |
| Payments: authorize/capture, refunds, disputes, SCA, currency | [payments.md](payments.md) |
| Real-world failure modes and how to reason about them | [failure-scenarios.md](failure-scenarios.md) |

Project documents to read alongside:
- `docs/requirements/glossary.md` (terms)
- `docs/architecture/booking-lifecycle.md` and `docs/architecture/payment-lifecycle.md` (our state machines)
- `docs/architecture/provider-integration.md` (ports, error taxonomy, mock scenarios)
- `docs/quality/failure-scenarios.md` (the canonical scenario catalog with expected behaviour and test level)

## Core domain truths (always apply)

1. **Search results are not inventory.** Prices and availability are quotes that change. Only a revalidated offer can be booked, and every offer expires.
2. **Supplier writes are not transactional with us.** A booking can succeed at the supplier while our call times out. Treat unknown as unknown, and reconcile by querying.
3. **Booked is not the same as ticketed or confirmed.** For flights, a PNR/order can exist without a ticket. For hotels, a request can be "on request" before confirmation.
4. **Times are local.** Flight times are airport-local, and hotel dates and cancellation deadlines are property-local or supplier-defined. Convert only for display, and keep the originals.
5. **Money has a currency, and suppliers may price in a different currency than the customer pays.** FX is a business decision with risk, not a formatting detail.
6. **Refund eligibility comes from fare rules and cancellation policies, not from our UI.** Always ask the supplier or the stored policy snapshot.
7. **Legal model matters.** Merchant of record and package-travel rules change payment, refund, and liability flows. See `docs/requirements/open-questions.md`.
