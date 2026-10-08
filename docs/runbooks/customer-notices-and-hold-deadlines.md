# Customer notices not sent, and payment holds about to lapse

Background: ADR 0024 (notifications), ADR 0025 (hold deadlines).

## Alert: `NotificationFailed`
**Severity:** P3 (P2 for a confirmation). **Owner:** customer operations.
**Customer impact:** the customer did not get the email about their booking. Their money and booking are unaffected.

**Diagnose** (all queries are read-only):
1. Look up the notice: `notifications.Notifications` by `OrderId`.
2. Check `Status`, `Attempts` and `LastError`. `LastError` is a reason code, never provider text or an address.
3. Check `Kind` to see which notice it is: `booking-confirmed`, `booking-partially-confirmed` or `booking-not-booked`.

**Resolve:**
- `Failed` with `invalid-recipient`: the address the customer gave is refused. Contact them through support (Q12), and correct it with the customer.
- `Failed` after 10 attempts with a temporary reason: the provider was unreachable for hours. Check the provider status. To resend once it is back, set the notice to `Pending` with a `NextAttemptAt` in the past. Do this as a ticketed operational change, never by editing the message.
- `Suppressed` with `no-contact`: the order's personal data is anonymised, so there is nobody left to tell. No action.

- No notice at all for a confirmed order: the `OrderBookingSettled` event itself gave up in the outbox (`FailedAt` set after 10 attempts). The customer has not been told about a booking they were charged for. Treat it as P2: tell the customer through support, then re-run the event as a ticketed operational change.

## Alert: `VoucherStayUnreadable`
**Severity:** P3. **Owner:** customer operations.
**Customer impact:** the booking confirmation was sent, but without the hotel stay's details (ADR 0030). The booking, the money and the cancellation terms are unaffected.

**Diagnose:** the alert names the order. Open it in admin-web: the order view shows the hotel stay and the agreed cancellation terms. If the stay is missing there too, the Hotels database (connection string `Hotels`) was unreachable or not configured for the Worker.

**Resolve:** send the customer the stay's details (hotel, dates, room, cancellation terms and booking reference) by hand through support. Fix the Worker's Hotels configuration if that was the cause.

## Warning: `NotificationProviderMissing`
No email provider is configured in this environment, so notices are waiting as `Pending`. Nothing is lost: they are sent once a provider is configured (ADR 0024). Production must not run like this. Until the email provider's account and sending domain exist, which is an external dependency, only Development and Staging have the recording stand-in.

## Alert: `PaymentHoldExpiring`
**Severity:** P2. **Owner:** payments operations.
**Customer impact:** the customer's funds are held. If the hold lapses before we capture, a confirmed booking cannot be charged.

The hold of an attempt lapses at its start (`AuthorizedAt`, or the attempt's start when that is not known) + `Payments:Holds:Lifetime`. The default lifetime is 7 days, a placeholder until the payment provider confirms it for our account. The alert fires once per attempt, `Payments:Holds:WarningBefore` (48 hours by default) before that. Settle it before the hold lapses:
1. Open the payment in admin-web (**Payment**). It shows the status, history and "Hold lapses".
2. **`ManualReview`:** check with the payment provider from the payment page (runbook `payment-hold-release.md`, Case B). If the order is in booking review, resolve the booking first (ADR 0025).
3. **`Authorized` with a confirmed booking but no capture:** see `payment-captured-booking-failed.md` and the capture reconciliation. A capture stuck in `CaptureUnknown` is looked up by the Worker.
4. **`Authorized` and nothing booked:** the release should already be requested; follow `payment-hold-release.md`.

The watch never captures or releases anything by itself. On its first run after deployment it warns about every live hold that is already past its warning time; this is expected.
