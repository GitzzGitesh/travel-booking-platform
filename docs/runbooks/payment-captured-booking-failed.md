# Booking confirmed but not charged (capture failed)

**Alert:** `PaymentCaptureFailed` (a capture refused, or a hold found lapsed, voided or for another amount while charging); `PaymentCaptureNotPossible` (Orders asked to charge an attempt that cannot be captured).
**Severity:** P1 (we hold a supplier booking we are not paid for).  **Owner:** payments operations
**Customer impact:** none visible. The customer is booked and sees the order as confirmed.

Background: ADR 0021, `docs/architecture/payment-lifecycle.md` (capture), F-23 and F-24 in `docs/quality/failure-scenarios.md`.

## Diagnose
All queries are read-only.
1. The attempt: `payments.PaymentAttempts` by `OrderId`, giving `Status` (ManualReview), `CaptureRequestedAt`, `CaptureAmount`, `ProviderPaymentId` and `ReleaseRequestedAt`.
2. Its history: `payments.PaymentAttemptEvents` by `PaymentAttemptId`, giving the capture request, `Capturing`, and the reason it went to review.
3. The provider's dashboard, by `ProviderPaymentId`. Is the payment captured, still authorized, lapsed or voided?
4. The order: `orders.FlightOrderItems` (Confirmed, locator) and the timeline note "Booking confirmed: … to be charged".

## Resolve
- **The provider shows it captured for the requested amount:** the charge happened and our record missed it. Resolve the review to what the provider holds through `ResolvePaymentReviewHandler` (engineering, until the operator endpoint exists).
- **Still authorized (not captured):** the capture can still be made with the same key (`{attempt}:capture`). Escalate to payments engineering; do not capture by hand in the dashboard.
- **Lapsed or voided hold (F-24):** we cannot charge this payment. Re-authorizing or cancelling the booking is a business decision that is still open. Escalate to payments operations and the supplier desk the same day: the supplier's free cancellation period may be short.
- **`PaymentCaptureNotPossible`:** a capture request reached an attempt that was not Authorized, or had a release requested. Compare the order's timeline with the attempt's history. This should not happen, so escalate to engineering.

## Do NOT
- Do not cancel the supplier booking automatically or on your own. The customer is booked.
- Do not capture, void or refund by hand in the provider's dashboard. Our record would then be wrong.
- Do not edit `PaymentAttempts` or delete history rows.

## Escalate
Payments engineering and payments operations, with the order id, the attempt id, the provider payment id and the correlation ids.
