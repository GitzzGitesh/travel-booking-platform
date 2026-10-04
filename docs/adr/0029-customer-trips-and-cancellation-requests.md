# 0029. Customer trips and cancellation requests

- **Status:** Accepted (2026-10-04) under the delegated decision authority of 2026-10-02
- **Date:** 2026-10-04
- **Deciders:** Engineering lead (delegated)
- **Related:** [0027](0027-refunds-and-cancellations.md) (cancellations and refund cases), [0024](0024-notifications-module-and-email-delivery.md), [0028](0028-customer-web-session-bff.md), `docs/runbooks/refunds.md`

## Context

Customers can book in customer-web (row 16) but cannot see their bookings again, and cannot ask to cancel one. ADR 0027 built cancellations for staff:
1. the supplier's desk cancels;
2. staff record it as a cancellation case, and the refund is computed by the server;
3. a second person approves it, and Payments refunds once.

Supplier cancellation through an API is blocked (Amadeus R8). Consumer cancellation rights and refund timelines per market are pending legal confirmation (Q15). The customer side must therefore not promise an outcome or an amount.

## Decision

1. **"My trips":** `GET /api/v1/orders?limit=&cursor=` lists the signed-in customer's own orders, newest first.
   - The cursor is the last order's creation time and id (as for staff lists); `limit` is 1 to 50.
   - It returns the same order resource as `GET /orders/{id}`, read-only.
2. **A cancellation request, not a self-service cancellation.**
   - `POST /api/v1/orders/{orderId}/cancellation-requests` with an `Idempotency-Key` records the customer's wish to cancel a booking that has a confirmed item.
   - It changes no booking and promises no refund.
   - It is a separate record in the Orders schema (`CancellationRequests`): `Open`, then `Completed`, `Declined` or `Withdrawn`.
   - **Idempotency:** one request per customer and key (unique index); a replay returns it; the same key for another order is 409 `idempotency-conflict`.
   - **At most one open request per order** (a filtered unique index). Another one is 409 `cancellation-already-requested`.
   - **Withdrawal:** the customer may withdraw an open request (`.../withdrawal`).
   - **Timeline:** every step goes on the order's timeline.
3. **Operations handle it with the existing flow (ADR 0027).**
   - They find open requests on `GET /api/admin/v1/cancellation-requests` (`refunds.request`, oldest first), cancel at the supplier's desk, and open the cancellation case.
   - Opening a cancellation case for the order completes its open request in the same save, once no confirmed item is left (a partial cancellation leaves it open).
   - A request withdrawn after staff called the desk cannot stop the cancellation. The case then flags it on the timeline for a person to tell the customer, and the withdrawal message promises nothing.
   - If the booking cannot be cancelled, a person declines the request with a ticket reference (`POST /api/admin/v1/cancellation-requests/{id}/decline`, audited). The customer is told only that it could not be cancelled and that support will contact them; the reason stays internal.
   - Nothing is automated against the supplier until supplier cancellation is verified (R8).
4. **Customer notices (ADR 0024):** "We have received your cancellation request" when one is opened, and "We could not cancel your booking" when one is declined. Completion is already told by "Your booking has been cancelled" (ADR 0027). No amount, deadline or right is stated.
5. **Pending legal confirmation:**
   - a response time target for requests (none is promised now);
   - whether some fares are non-cancellable online;
   - the customer's rights per market.

## Consequences

- Customers get one place for their bookings and a clear way to ask for a cancellation. Staff keep a single, audited path that already enforces maker-checker and once-only refunds.
- Requests that sit open are visible on an operations list. An alert on old open requests waits for a response target to be set (legal).
- A customer whose fare cannot be cancelled hears it from a person, not from a rule we do not have yet.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Self-service cancellation with an instant quote | Needs the supplier's cancellation API and refund rules (R8, Q15). It would also guess at the refund |
| A free-text reason from the customer | Invites personal data into an operations record. Support channels handle details (Q12) |
