# Product scope

**Status: Draft.** Needs product owner review.

## Vision
A global travel booking platform where customers search, book, and pay for flights and hotels, and an operations team manages bookings, payments, suppliers, pricing, and customers safely.

## Actors
| Actor | Description |
|---|---|
| Guest | Unauthenticated visitor: search and browse |
| Customer | Registered user: books, manages bookings and saved travellers |
| Traveller | A person travelling (may not be the customer). Holds passenger/guest data |
| Support agent | Staff: views bookings and timelines, assists customers, triggers approved actions |
| Operations/finance | Staff: refunds, reconciliation, supplier issues |
| Pricing manager | Staff: markups, promotions |
| Administrator | Staff: users, roles, permissions, provider configuration |
| (Future) B2B agent | External agency user booking on behalf of clients |

## Customer capabilities
- Search flights (one-way, return, multi-city later) and hotels (destination/property, dates, occupancy)
- View offer details: price breakdown, fare rules/cancellation policy, baggage, board basis
- Book with price revalidation, and pay by card (SCA-capable)
- Manage account, saved travellers (with consent), and booking list and details
- Receive confirmation, e-tickets, and hotel vouchers by email and in-account
- Cancel eligible bookings and see the refund amount before confirming
- Track refund status
- Be notified of supplier-initiated changes (schedule changes)

## Admin / operations capabilities
Customers · flight bookings · hotel bookings · booking timeline · payments · refunds (with approval) · providers (configuration and health) · pricing and markups · promotions · reports · audit log · users, roles, and permissions · operational queues (pending confirmations, failed captures, reconciliation mismatches, schedule changes)

## Phasing (proposed)
1. **MVP slice**: one product (see open question Q3) end-to-end with mock providers, card payment via Stripe test mode, confirmation email, minimal admin (booking and timeline view).
2. Second product and combined orders (subject to Q2 package-travel implications).
3. Cancellations and refunds self-service; full admin; reporting.
4. B2B/agent capabilities.

## Explicitly out of scope (for now)
Loyalty programmes · car hire, rail, insurance, activities · corporate travel policies · offline/call-centre bookings · native mobile apps · alternative payment methods beyond cards (review later: wallets and local methods matter by market) · multi-tenant white-label.

## Related
- [Non-functional requirements](non-functional.md)
- [Open questions](open-questions.md)
- [Glossary](glossary.md)
