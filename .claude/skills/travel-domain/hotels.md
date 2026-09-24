# Hotels

## Distribution models
- **Bed banks / wholesalers** (e.g. Hotelbeds): net rates. We add markup, pay the wholesaler, and the customer pays us. We are usually the merchant.
- **OTA affiliate/partner APIs** (e.g. Expedia Rapid): can be merchant model (we collect payment) or agency model (pay at property). Contract-specific.
- **Direct / channel managers**: rare at launch.

## Key concepts
- **Property → room type → rate plan**: the same room can have many rate plans (board basis, refundability, payment model, promotional conditions).
- **Board basis**: RO (room only), BB (bed & breakfast), HB (half board), FB (full board), AI (all inclusive).
- **Occupancy**: rooms × (adults + children with ages). Child ages affect price and availability. Always send ages.
- **Rate types**: *refundable* (free cancellation until a deadline, then penalties), *non-refundable*, *pay at property* (we don't take the room payment; card may be a guarantee only).
- **Cancellation policy**: a list of deadlines and penalties (e.g. free until 3 days before check-in 14:00 **hotel local time**, then 1 night, then 100%). Store the **policy snapshot at booking time**. Suppliers change policies.
- **Check-in/out**: local dates (not instants). Stay length = nights.
- **Taxes and fees**: some are included, and some are **payable at the property** (city/resort fees). Price-display laws often require showing them up-front.
- **Rate check / prebook**: most APIs require re-checking the chosen rate right before booking. The price or policy can change.
- **Confirmation**: some bookings confirm instantly, and some are **"on request"** (pending hotel confirmation).
- **Voucher**: the document the guest shows at check-in. Includes supplier reference, hotel confirmation number (may arrive later), and payable-at-property amounts.

## Things that go wrong in reality
- **Rate changed or unavailable at prebook**: common, and must be surfaced as a price/policy change.
- **Timeout on book**: the reservation may exist. Query by our client reference. Never re-book blindly (it could double-book the room).
- **Hotel confirmation number missing** at booking and arriving later (asynchronous update).
- **Overbooking/walk**: the hotel cannot honour the booking. This is an operational/support process, with the supplier liable depending on the contract.
- **Cancellation deadline confusion**: computed in the wrong time zone, so the customer is charged a penalty. Keep the supplier's deadline and zone verbatim.
- **Mapping issues**: the same property appears under different supplier IDs. A content/mapping layer is needed when multiple hotel suppliers exist.

## Modelling implications for us
- `HotelBooking` (an Order item) stores: property and room/rate snapshot, occupancy, **local check-in/out dates**, board basis, price breakdown (including pay-at-property amounts), **cancellation policy snapshot with zone**, supplier reference, hotel confirmation number (nullable, updated later), and status per `docs/architecture/booking-lifecycle.md`.
