# 0006. Payments: Stripe first, server-confirmed authorization, PCI SAQ-A, manual capture, webhooks

- **Status:** Accepted (2026-10-05) under the delegated decision authority (the project owner asked for business blockers to be decided as for a production application, and noted). Stripe is the first payment provider, with server-side confirmation (option A below). Still external before live payments: the merchant entity (Q14, P1), Stripe's account approval (P2) and test-mode keys (P3). (Proposed 2026-09-24, revised 2026-09-27.) The provider-neutral groundwork and a Stripe adapter mapped from documentation are implemented, but the adapter is **not production-ready**: startup refuses it outside Development and Staging, and it accepts test-mode keys only.
- **Date:** 2026-09-24, revised 2026-09-27
- **Related:** [0004](0004-supplier-provider-abstraction.md), [0005](0005-order-aggregate-and-booking-orchestration.md), [0007](0007-async-processing-worker-and-outbox.md), [0010](0010-money-currency-and-time.md), [0015](0015-synchronous-cross-module-commands-for-checkout.md), `docs/architecture/payment-lifecycle.md`, `docs/requirements/open-questions.md` (Q1, Q2, Q5, Q10, Q14)

## Context

We are the merchant of record for flights (Q1). The target markets are Africa, India, Tunisia, the Middle East, Europe, and worldwide customers (Q2).

The currency policy (Q5):
- charge in USD, EUR or GBP;
- INR and TND once the merchant and payment provider support them is confirmed;
- USD wherever the local currency is unavailable.

The payments core already exists and is provider-neutral:
- `IPaymentProvider` (authorize with manual capture, capture, void, refund, and lookups by our reference and key);
- persistent payment attempts, saved before every provider call, with a unique idempotency key and at most one live hold per order;
- reconciliation and hold release in the Worker (ADR 0007);
- a deterministic mock.

What is missing is the real provider, how a customer challenge (SCA/3DS) is confirmed, and how provider notifications (webhooks) enter the system.

## Provider evaluation

Sources: the providers' public documentation and availability pages, read on 2026-09-27. Fees are not compared: they are contract-specific.

Each fact is marked **C** (confirmed in the provider's documentation), **U** (unsupported), **CC** (requires commercial confirmation), **ME** (requires confirmation for our merchant entity), or **PC** (requires provider confirmation).

| Topic | Stripe | Adyen | Checkout.com |
|---|---|---|---|
| Merchant accounts (entity countries) | C: self-serve in the UK, EU, US and UAE among others. India: preview, invite-only (CC). Tunisia, Egypt, Morocco: U. Nigeria, South Africa and Kenya only through Paystack, a separate product (U for this integration) | CC: sales-led onboarding. Entity and acquiring countries per contract | CC: sales-led. Operates in the UK, EEA, MENA, APAC and US, and holds a UAE acquiring licence (C) |
| Worldwide card acceptance | C: international cards, cross-border from our entity's country (issuer approval rates vary: PC) | C per contract | C per contract |
| India | Cards issued in India are cross-border for a non-Indian entity (ME/PC: issuer approval and RBI rules). INR presentment exists (C); for our account: ME. Search API not available to Indian businesses (C) | Local acquiring: CC | CC |
| Tunisia | No Tunisian entity (U). TND is listed as a three-decimal presentment currency (PC for our account). The dinar is not freely convertible, so local acquiring normally needs a Tunisian entity (ME) | CC | CC |
| Africa | Cross-border card acceptance (C). Local methods through Paystack only (U here) | CC | CC |
| USD / EUR / GBP | C | C | C |
| INR / TND | INR: C (presentment), ME. TND: PC, ME | CC | CC |
| Manual capture, authorization life | C: `capture_method=manual`. About 7 days for online card-not-present payments (Visa merchant-initiated: 5 days). Extended authorizations only where eligible (PC) | C: configurable, network rules apply | C |
| Void, refund, partial capture | C: cancel; refunds as their own objects; `amount_to_capture` (a partial capture releases the rest) | C | C |
| SCA / 3DS, hosted fields | C: Payment Element (SAQ-A); ConfirmationTokens let our server confirm; `handleNextAction` runs the challenge | C: Drop-in / Components | C: Frames / Flow |
| Idempotency | C: `Idempotency-Key` on every POST. Keys are kept at least 24 hours, and the first result is replayed, including a 500. Other parameters with the same key are an error | C | C |
| Lookup by our reference | C: metadata search. It lags writes (normally under a minute), so "not found" needs a window. A direct read by PaymentIntent id is consistent | C | C |
| Webhooks | C: HMAC-SHA256 signature with a timestamp (replay tolerance); retried for up to 3 days; no ordering guarantee; duplicates possible | C | C |
| Fraud tooling | C: Radar (rules per Q10); advanced features: CC | C: RevenueProtect | C |
| Settlement currencies | ME: depend on the entity's country and bank accounts | CC | CC |
| Travel / airline category | CC: our category, possible reserves given the delay between payment and travel, and whether we pass airline data | C: airline data supported. Terms: CC | CC |
| Test environment without a contract | **C: self-serve test mode** | CC: test account on request | CC |
| Fit with our port | 1:1: manual capture, cancel as void, keyed writes that replay, lookup by metadata, signed events | Good | Good |

Regional acquirers are not a first global provider. They become candidates behind the same port once local entities exist:
- Paystack or Flutterwave in Africa;
- Razorpay in India;
- local Tunisian acquiring.

## Decision

1. **Provider: Stripe first (recommended; acceptance is the project owner's).**
   - It is the only candidate whose test mode is self-serve, so the adapter can be verified against the real API before any contract, unlike Amadeus (ADR 0019).
   - Its documented primitives map one-to-one onto our port.
   - USD, EUR and GBP are confirmed.
   - Its gaps are clear, not hidden: no local acquiring in Tunisia, India or most of Africa, and cross-border approval rates there. They are addressed later by a second provider for regional acquiring (Adyen, Checkout.com, or a local acquirer) behind the same port. That needs its own ADR, because today exactly one payment provider is composed.
2. **Confirmation model: A, server-confirmed authorization with a token.**
   - The browser's Payment Element creates a ConfirmationToken. Card data goes to Stripe only (SAQ-A).
   - The token is sent with the checkout step, not with an amount.
   - Our server creates and confirms the PaymentIntent in one idempotent call: the amount is the order's server-side total, `capture_method=manual`, the Idempotency-Key is our payment reference, and the reference is stored as metadata.
   - A challenge comes back as `ActionRequired` with the client secret, returned only to the order's owner. The browser completes it with `stripe.handleNextAction`.
   - The outcome is never taken from the browser: it is learnt from a lookup (the customer repeating the step with the same key, or reconciliation) or from a notification that triggers a lookup.

   **B (browser-confirmed)** was rejected:
   - the authorization would start in the browser, outside our attempt record, so the "saved before the provider call" invariant, our idempotency key on the confirming call and the one-live-hold rule would all depend on the client;
   - it gives no gain in PCI scope (both are SAQ-A) or in SCA support (both run the challenge in the browser).
3. **Port adjustments (provider-neutral, small):**
   - `IPaymentProvider.IsProductionReady` defaults to false (fail closed). Startup refuses a provider that is not production-ready outside Development and Staging, and refuses more than one provider.
   - `RetrieveAsync(reference, knownPayment)` is a direct read when the provider's payment id is known. It defaults to the lookup by reference. A consistent lookup after a challenge, instead of waiting out search lag.
   - `IPaymentProvider.MinimumNotFoundWindow`: the shortest "not found" window the provider's lookup by reference needs. Startup refuses a shorter `Payments:Reconciliation:NotFoundConclusiveAfter`. Stripe declares **1 hour**: its search normally lags by under a minute, but by much more during incidents, and concluding "not found" too early would let a second hold start.
   - `IPaymentNotifications` is the webhook port. It verifies a notification over the raw body and yields only the event id, the kind, our reference and the provider's payment id.
4. **Webhooks:**
   - `POST /api/v1/payments/notifications/{providerId}` is anonymous, because the provider's signature authenticates it. It is mapped only when the composed provider sends notifications, and it is excluded from OpenAPI.
   - The body is limited to 64 KB. The notification is verified before anything is stored, and a rejection is logged as a security event.
   - One row is stored per provider event, enforced by a unique constraint, and the endpoint answers 200 fast. The row also keeps the event type and the provider's creation time, both non-personal, for operators.
   - The Worker job then reconciles the attempt: a **lookup** with the provider, never the event's content. The provider payment id the event carries is only a hint for a direct read, and it is checked against our reference. So duplicate, late and out-of-order events cannot move a payment by themselves.
   - **Holds on settled attempts:** a notification about a settled attempt is looked up too. If the provider reports a hold on an attempt we consider over (declined, failed, canceled, voided or expired), the hold is voided at once with its own key (`{reference}:stray-void`), recorded on the attempt's history and logged as an alert. If the void fails, the notification is retried and then given up with the alert. The attempt stays final, so it never competes with the order's live attempt.
   - **Deviations to confirm (the owner's decision; the rule files change only after that):**
     - The notification row carries processing markers (processed at, attempts, outcome), like the outbox's, where `database.md` treats inbox tables as append-only.
     - The booking rules say "persist the raw event". We store only the verified fields above. Raw Stripe events can carry personal data (billing details), and retention is undecided (Q9). Processing never reads them, because it looks the payment up. Stripe keeps events retrievable by id for 30 days only, so the raw event does not cover disputes or audits after that. The finance reconciliation relies on our records and Stripe's balance transactions instead.
5. **Unchanged:**
   - manual capture, with capture only after the supplier confirms the booking (ADR 0005);
   - card data only through Stripe's hosted fields, and we store provider ids only;
   - idempotency keys derived from our ids;
   - refunds as separate records;
   - secrets in Key Vault, and only the publishable key in the frontend;
   - Radar enabled, with rules per Q10.
6. **Adapter (`Integrations.Payments.Stripe`):**
   - plain HTTP (form-encoded) with the pinned `Stripe-Version` header, and no Stripe SDK (no new dependency);
   - cards only (`payment_method_types[]=card`), because every card supports manual capture;
   - accepted tokens: `ctoken_` (ConfirmationTokens) and `pm_` (test-mode PaymentMethods);
   - two-decimal currencies only, converted to minor units without rounding (ADR 0010). TND and other three-decimal currencies are refused until Stripe confirms the rules for our account;
   - test keys only (`sk_test_` / `rk_test_`) until production-ready.
   - Mapping:
     - `requires_capture` → Authorized;
     - `requires_action` → RequiresAction;
     - `requires_payment_method` → Declined;
     - `succeeded` → Captured;
     - `canceled` → Voided, Expired or Canceled, depending on whether a hold existed and on the cancellation reason (to confirm in test mode);
     - a card decline (402 carrying the PaymentIntent) → Declined.
   - **A decline is closed before it is reported.** A `requires_payment_method` PaymentIntent is not final at Stripe: its client secret could still confirm it with another card. The adapter cancels it first (key `{reference}:close-declined`) and reports Declined only when that succeeded. Otherwise the answer is unknown and the next lookup tries again. A closed decline reads as Declined.
   - Authorized reports the capturable amount, so a partial authorization shows as a different amount (then ManualReview).
   - A void that finds the PaymentIntent already canceled reads it and reports its state (Voided, Expired or Canceled).
   - Error mapping: idempotency errors by code (a key reused with other parameters is definitive; a key still in use is unknown). Any other 409 on a write is unknown. Otherwise the shared status mapping applies, where a write's 5xx or timeout is Unknown. Stripe's messages are never copied.
   - `pm_` tokens are accepted only with a test-mode key.

## Currencies

| Currency | Status |
|---|---|
| USD, EUR, GBP | Supported by the adapter. Enable each for the account in configuration (`Currencies`) |
| INR | Supported by the adapter (two decimals). Enable only after Stripe confirms INR presentment for our entity (ME) |
| TND | **Refused by the adapter.** It needs Stripe's three-decimal rules for our account (PC) and, for local acquiring, a Tunisian entity (ME). Until then, Tunisian customers pay in USD, the Q5 fallback |

## Before production (P1–P10)

| # | Requirement | Owner |
|---|---|---|
| P1 | Merchant entity and country for the Stripe account (Q14): settlement currencies, fees, INR eligibility. **Hard dependency:** Stripe search, which the lookup by our reference relies on, is not available to accounts in India, so an Indian entity needs another design | Business owner |
| P2 | Stripe account approval for our category (travel agency, merchant of record for flights), any reserve, pricing (standard or IC+), airline data | Business owner with Stripe |
| P3 | Test-mode keys, then the shared contract suite run against test mode (`STRIPE_TEST_SECRET_KEY`, `STRIPE_TEST_API_VERSION`). It confirms the mapping (cancellation reasons, 3DS test methods, search lag), whether server-side confirmation of a 3DS card needs `use_stripe_sdk` or `return_url`, and whether `amount_refunded` counts the released remainder after a partial capture | Engineering |
| P4 | A public HTTPS webhook endpoint (hosting, Q2), its signing secret in Key Vault, and only the event types we use | Engineering |
| P5 | The pinned API version recorded, and an upgrade procedure | Engineering |
| P6 | Authorization lifetime against our booking time; eligibility for extended authorization | Stripe (PC) |
| P7 | INR and TND enablement (see Currencies) | Stripe, business owner |
| P8 | Radar rules and the fraud review process (Q10) | Business owner |
| P9 | The customer payment step in customer-web: Stripe.js loaded from js.stripe.com, the Payment Element, CSP updates and the publishable key. It comes with the checkout page (identity, ADR 0008). **Built 2026-10-05, not yet run against Stripe:**
- The Payment Element (cards only, manual capture, like the PaymentIntent) creates a ConfirmationToken in the browser, and our server confirms with it.
- A 3-D Secure check is completed with `handleNextAction` and the same request repeated once under the same key; the server's answer decides the outcome.
- The publishable key comes from `Integrations:Payments:Stripe:PublishableKey` (`pk_test_` only for now).
- **To verify in P3:** the Elements options against the PaymentIntent's parameters, and 3DS with server-side confirmation.
- **CSP:** customer-web sends none yet. The production CSP comes with hosting (Q2) and must follow Stripe's own CSP documentation at that time; this ADR does not fix the list | Engineering |
| P10 | Live keys in Key Vault, and `IsProductionReady` switched on only after P1–P9 | Engineering, with business sign-off |

## Consequences

**Positive:**
- minimal PCI burden, and voids instead of refunds for booking failures;
- the authorization is always recorded and keyed on our side;
- webhooks can only prompt lookups, so they are safe under duplication and reordering;
- the adapter can be verified in Stripe test mode without a contract.

**Negative:**
- the authorization hold expires (about 7 days), which constrains delayed-confirmation flows (F-24);
- regional acquiring (Tunisia, India, much of Africa) needs a second provider later;
- Stripe's search lag means "not found" is conclusive only after an hour (enforced at startup): an authorization whose outcome is unknown can keep the order's payment step pending that long.

If the airline becomes merchant of record (card pass-through to BSP), SAQ-A no longer holds and this ADR must be revisited.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Adyen or Checkout.com first | Strong for travel and MENA, but onboarding is sales-led and there is no test environment before a contract, so nothing can be verified now. Candidates for regional acquiring later |
| Regional acquirers first | Single-region, and they need a local entity. Not a worldwide first provider |
| Browser-confirmed payment (B) | See decision 2 |
| Stripe SDK | A new dependency for a handful of form-encoded calls. Plain HTTP keeps the adapter small and its wire model explicit |
| Automatic capture | Booking failures become refunds (fees, delay, disputes) |
| Server-side card collection | Moves us to SAQ-D. Unacceptable cost and risk |
| Webhook-only state (no direct retrieval) | Webhooks can be delayed or lost; reconciliation needs pull as well |
| Storing raw webhook events | Personal data with no retention decision (Q9), and never needed, because processing looks the payment up |
