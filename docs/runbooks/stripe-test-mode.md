# Stripe in test mode (ADR 0006)

**When:** verifying the Stripe adapter (P3 in ADR 0006), or running the Api and Worker locally against Stripe instead of the mock.
**Owner:** engineering.
**Customer impact:** none. Test mode only: the adapter refuses live keys, and startup refuses it outside Development and Staging.

## Configure (never in appsettings or Git)
Set these with user-secrets locally, or in Key Vault for Staging, for **both** hosts (Api and Worker):
- `Integrations:Payments:Stripe:Enabled=true` (the mock is then not composed);
- `Integrations:Payments:Stripe:BaseUrl=https://api.stripe.com/`;
- `Integrations:Payments:Stripe:SecretKey`: `sk_test_...` or `rk_test_...` (a secret);
- `Integrations:Payments:Stripe:WebhookSigningSecret`: `whsec_...` (a secret: from the webhook endpoint, or from `stripe listen` locally);
- `Integrations:Payments:Stripe:ApiVersion`: the pinned API version;
- `Integrations:Payments:Stripe:Currencies:0=USD` (add EUR and GBP as needed; INR only after P7; TND is refused).

For Stripe, set `Payments:Reconciliation:NotFoundConclusiveAfter` to at least `01:00:00` (startup refuses less): Stripe's search can lag its writes.

**Alert `StrayPaymentHold`:** a notification revealed a hold on an attempt we consider settled. The hold is voided automatically and recorded on the attempt's history. If the log also says "NOT released", void it by hand in the Stripe dashboard and record it with finance.

## Verify the adapter (P3)
1. Set `STRIPE_TEST_SECRET_KEY` and `STRIPE_TEST_API_VERSION`, then run `dotnet test --project tests/backend/ProviderContracts`. Without them, `StripeSandboxContractTests` report as skipped, never as passed.
2. Where test mode differs from the fixtures (for example cancellation reasons, 3DS test methods or search lag), fix the mapping and update `StripePaymentProviderTests` to the real (anonymised) shapes.
3. Webhooks locally: `stripe listen --forward-to localhost:5080/api/v1/payments/notifications/stripe`. Use the signing secret it prints, and subscribe only to `payment_intent.*` and refund events.

## Do NOT
- Do not use live keys, or real cards: use Stripe's test payment methods only.
- Do not set `IsProductionReady` before P1–P9 in ADR 0006 are done.
- Do not log request bodies on the notification route, or copy Stripe's error messages into ours.

## Escalate
Engineering lead. For account, category or currency questions (P1, P2, P7), the business owner.
