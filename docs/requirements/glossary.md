# Glossary

| Term | Meaning |
|---|---|
| **Offer** | A priced, time-limited proposal from a supplier (flight itinerary + fare, or hotel room + rate) for specific travellers. Must be revalidated before booking |
| **Offer snapshot** | Our persisted copy of an offer (price breakdown, rules, expiry) used for server-side pricing and audit |
| **Revalidation / price check / prebook** | Re-querying the supplier for the selected offer's current price and availability just before payment |
| **Order** | Our customer-facing trip: one checkout containing one or more Order items and their payments |
| **Order item** | A single bookable product in an Order (FlightBooking, HotelBooking, later ancillaries) with its own supplier and lifecycle |
| **PendingConfirmation** | State when a supplier write's outcome is unknown (e.g. timeout). Resolved by reconciliation, never by resubmitting |
| **Reconciliation** | (1) Resolving unknown booking/payment states by querying the provider; (2) financial matching of our records against Stripe and supplier statements |
| **Timeline** | Append-only history of everything that happened to an Order (state changes, supplier calls, payments, notifications, admin actions) |
| **Idempotency key** | Client- or server-generated key that makes repeating a command safe: the same key returns the same result |
| **Outbox / Inbox** | Tables that guarantee reliable async messaging: outbox commits events with the domain change; inbox dedupes incoming events/webhooks |
| **Provider / supplier** | External system supplying inventory (airline, aggregator, GDS, bed bank) or payments (Stripe) |
| **Provider port** | Our interface (`IFlightProvider`, `IHotelProvider`, `IPaymentProvider`) that each supplier adapter implements |
| **GDS** | Global Distribution System (Amadeus, Sabre, Travelport) |
| **NDC** | IATA New Distribution Capability: an XML/JSON offer-and-order standard for airline retailing |
| **PNR / record locator** | Passenger Name Record: the airline/GDS booking record and its 6-character reference |
| **E-ticket** | Electronic ticket (13-digit number) proving right to travel. Issued after the PNR is created |
| **EMD** | Electronic Miscellaneous Document: used for ancillaries (bags, seats) |
| **TTL (ticketing time limit)** | Deadline to issue tickets before the airline cancels an unticketed PNR |
| **Void** | Cancelling a ticket (or a card authorization) without financial settlement, usually within a short window |
| **Fare rules / fare basis** | Conditions attached to a fare: changes, refunds, baggage, validity |
| **Passenger types** | ADT adult, CHD child, INF infant (lap), YTH youth, SRC senior |
| **APIS / Secure Flight** | Advance passenger information (document data) required by authorities |
| **Schedule change** | Airline-initiated change to a booked flight (involuntary) |
| **BSP / ARC** | IATA settlement systems for agency ticket sales (ARC in the US) |
| **Bed bank** | Hotel wholesaler selling net rates (e.g. Hotelbeds) |
| **Rate plan** | A hotel price offering for a room type with conditions (board, refundability, payment model) |
| **Board basis** | RO room only, BB bed & breakfast, HB half board, FB full board, AI all inclusive |
| **Cancellation policy** | Deadlines and penalties for cancelling a hotel booking, expressed in supplier/hotel time |
| **Voucher** | Document the hotel guest presents at check-in |
| **Merchant of record (MoR)** | The legal entity that charges the customer's card and carries chargeback liability |
| **Authorization / capture** | Reserving funds on a card, then collecting them (manual capture) |
| **SCA / 3-D Secure** | Strong Customer Authentication required for many card payments in the EU/UK |
| **Chargeback / dispute** | A card-network reversal initiated by the cardholder's bank |
| **Markup** | Amount added to a supplier's net price |
| **Maker-checker** | Two-person control: one staff member requests, another approves |
| **Package travel** | Combining travel services (e.g. flight + hotel). Regulated in the EU (PTD) and UK (ATOL) |
