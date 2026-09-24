# Flights

## Distribution models
- **GDS** (Amadeus, Sabre, Travelport): PNR-centric. Search → price (fare quote) → create PNR (segments sold, passengers, contacts) → ticket (issue e-ticket against a stored fare) within a **ticketing time limit (TTL)**. Ticket issuance is settled through **BSP** (most markets) or **ARC** (US) if the agency is accredited.
- **NDC / Offer-Order** (IATA NDC; aggregators such as Duffel): the airline returns **Offers** (priced bundles with an expiry) and creates an **Order** on acceptance. Payment and ticketing may be part of order creation. This model maps most cleanly onto our Order design.
- **Aggregators/consolidators** may act as merchant of record and hold a balance or credit line, which changes the payment flow (see `payments.md`).

## Key concepts
- **Offer**: priced itinerary for specific passengers, with an expiry (minutes to hours). Must be **revalidated** before payment, because the price can change or the fare can sell out.
- **Segment / slice (journey)**: a segment is one flight leg; a slice is origin→destination possibly with connections. Round-trip = 2 slices.
- **Fare basis / fare family / brand**: determine baggage, change/refund rules, and seat selection. Always show **fare rules and baggage** before purchase (legal requirement in many markets).
- **PNR / record locator**: the 6-character booking reference. The airline locator can differ from the GDS/aggregator locator. Store both.
- **E-ticket number**: 13 digits (3-digit airline prefix + 10). One per passenger (plus conjunction tickets for long itineraries). **EMDs** cover ancillaries (bags, seats).
- **Void window**: tickets can often be voided without penalty the same day or within about 24h (varies by carrier/market). After that, cancellation becomes a refund subject to fare rules.
- **Ticketing time limit**: an unticketed PNR is auto-cancelled by the airline after the TTL. Price is typically not guaranteed until ticketing.
- **Passenger types**: ADT (adult), CHD (child), INF (infant on lap, no seat, must be paired with an adult), sometimes youth/senior. Age is judged **at the travel date**, not the booking date.
- **Names**: must match the travel document exactly. Name changes are usually not allowed (at most minor corrections). Validate length and characters to the supplier's rules.
- **APIS / secure flight data**: document number, nationality, date of birth, gender, expiry. Required for many international routes and some domestic ones (US Secure Flight). This is **Sensitive PII**.
- **Ancillaries**: seats, bags, meals, priority. Priced separately and may be ticketed via EMD.

## Things that go wrong in reality
- **Price change between search and booking** (very common). This is why revalidation is mandatory.
- **Sold out at booking time**, even after successful revalidation (race with other sellers).
- **Timeout after the PNR is created**: the booking exists but we don't know it. Query by our reference; never re-book.
- **Ticketing failure after the PNR is created**: a booked but unticketed PNR must be ticketed before the TTL, or cancelled and the payment voided/refunded.
- **Schedule changes (involuntary)**: airlines change times, flight numbers, or cancel flights days to months later. The customer must be notified, may accept, or may have the right to a refund or rebooking. This requires ingesting supplier notifications or polling orders. **Plan for it; it is not optional in production.**
- **Split tickets / multi-carrier itineraries**: separate tickets have no protection if one misses a connection. Label them clearly.
- **Duplicate bookings** by the customer across sessions: airlines may auto-cancel duplicates (same name/flight), which can cancel both.
- **Partial refunds**: taxes are often refundable even when the fare is not.

## Modelling implications for us
- `FlightBooking` (an Order item) stores: the offer snapshot (price breakdown, fare rules, baggage, expiry), passengers (by reference), segments with **local times + airport codes**, provider order ID, airline locator(s), ticket numbers per passenger, and a status following `docs/architecture/booking-lifecycle.md`.
- Keep supplier-specific identifiers in a provider reference structure, not as first-class domain fields named after a supplier.
