# 0032. Shared mock payment state in SQL

- **Status:** Accepted (2026-10-09), decided by the product owner after an architecture review
- **Date:** 2026-10-09
- **Deciders:** Product owner; engineering lead
- **Related:** [0004](0004-supplier-provider-abstraction.md) (provider ports and mocks), [0006](0006-payments-stripe-manual-capture.md), [0007](0007-async-processing-worker-and-outbox.md) (Api and Worker), `docs/architecture/provider-integration.md`, `docs/runbooks/stripe-test-mode.md`

## Context

The Api and the Worker are separate processes (ADR 0007). The checkout in the Api authorizes the payment. The Worker
captures it once the booking is confirmed, voids it when the booking fails, and executes approved refunds. The mock
payment provider kept its payments in process memory, so the Worker's mock could not see what the Api's mock had
authorized:
- captures were refused ("Capture refused (InvalidRequest)"), and attempts ended in ManualReview;
- captured-payment cancellations and refunds could not be completed locally;
- Staging could not be used for payment testing.

Only the same-process integration tests covered these flows end to end. Stripe test mode would cross the boundary, but
it needs external credentials and is not how the platform is developed day to day.

## Decision

We will keep the mock payment provider's state in SQL so that every process using the mock sees the same payments.

1. **Storage:**
   - The state lives in its own schema, `paymentsmock`, in the Payments database (connection string `Payments`).
   - The schema is owned by `Integrations.Payments.Mock`, through its own small `MockPaymentsDbContext` and migrations (`Persistence/Migrations`, history table `paymentsmock.__EFMigrationsHistory`).
   - No module reads or writes it. It is the mock's "provider system", like a supplier's own database.
2. **Only the mock changes:**
   - The payment port (`IPaymentProvider`), the Payments module and its schema do not change.
   - The provider's logic is unchanged: deterministic, chosen by the payment method token, idempotent by our keys.
   - Only its state moves behind a small `IMockPaymentLedger`.
3. **Two ledgers:**
   - `Shared` (SQL): the default, used by the hosts.
   - `InProcess`: the former behaviour, for unit and provider contract tests. It is selected by `Integrations:Payments:Mock:State`.
4. **Serialisation:**
   - Each mock call is one transaction that first takes an exclusive application lock (`sp_getapplock`) on its payment reference.
   - Calls for one payment run one at a time across processes, as a real provider serialises requests. Calls for different payments never wait on each other.
   - The rows are the mock's simulated provider state, not one of our aggregates, so they carry no `rowversion`. The lock is the concurrency control.
   - Operation keys are primary keys, so a replay returns the first result and another use of a key conflicts.
5. **Failure handling:**
   - A state store that cannot be read or written makes the provider **Unavailable** (nothing was done).
   - A commit that may have reached the database is an **Unknown** outcome, so the caller reconciles by our reference and does not resubmit.
6. **Development and Staging only:**
   - The mock, and therefore its state, is composed only in Development and Staging.
   - The Api's and the Worker's composition allow-list it, and `AddMockPaymentProvider` refuses any other environment at startup.
   - The startup composition check refuses a provider that is not production-ready outside those environments.
   - Production never falls back to the mock: without a production-ready provider it has no payment provider at all, and payment operations fail at first use (they never reach a mock). A startup failure for "no provider" waits until Production is meant to start (Q2).
   - The design-time factory refuses any declared environment (`ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`) other than Development or Staging, an allow-list as in the hosts. It is best-effort: the pipeline's own exclusion is the control.
   - The `paymentsmock` migrations are applied only to Development and Staging databases. They are never applied by the production pipeline, and never at startup.
7. **Architecture rule:**
   - `Adapters_and_contracts_do_not_use_the_data_or_web_frameworks` makes one exception: the mock's `Persistence` namespace.
   - `Only_the_mock_payment_provider_uses_its_state` keeps everything else out of that namespace.

## Consequences

**Positive**
- Checkout in the Api, then capture, void, cancellation and refund in the Worker, work locally and in Staging with no external credentials and no real money.
- The scenarios cross the process boundary: a capture whose answer was lost (F-23) is retried by the Worker under the same key, and a refund refused once (F-43) succeeds on retry.
- The ProviderContracts payment suite still runs against the mock, with the in-process ledger.

**Negative / trade-offs**
- **Migration:** local and Staging databases need one more migration (`Integrations.Payments.Mock`). Without it the mock reports itself unavailable, and payment attempts are reconciled or reviewed as for an outage.
- **Persistence in an adapter:** an integration project now holds persistence, by an explicit and tested exception to the adapter rule.
- **Calls are serialised per payment:** the application lock adds a round trip to each mock call (the mock does not need to scale).

**Follow-ups**
- The production deployment pipeline (no hosting decision yet, Q2) must leave out the mock project's migrations. That is recorded in the runbook.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Disable the Payments jobs outside Production | Captures, voids and refunds could then never be tested end to end; the Worker's money paths would stay unexercised. |
| State in the `payments` schema | The mock would own part of a module's schema, breaking one schema per module, and its tables would be created in Production. |
| A file or a cache shared by the processes | It does not work across Staging containers, and it gives no transactional serialisation. |
| Stripe test mode for local work | It needs external credentials and network access, and its outcomes are not scenario-driven or deterministic. |
