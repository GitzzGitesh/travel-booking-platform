# Mock payment provider: shared state (ADR 0032)

**When:** setting up a Development or Staging database. Also when payment attempts in those environments end in `ManualReview` because "the mock payment state could not be read or kept".
**Owner:** engineering.
**Customer impact:** none. The mock runs only in Development and Staging, with test payment methods and no real money.

## What it is
The mock payment provider (`mockpay`) keeps its payments in its own schema, `paymentsmock`, in the Payments database. The Api and the Worker therefore see the same payments: the Worker captures, voids and refunds what the Api's checkout authorized. Payments survive restarts.

## Set up a Development or Staging database
Apply the mock's migrations along with the modules' migrations, and only to a Development or Staging database:

```
dotnet ef database update --project src/backend/Integrations/Payments/Integrations.Payments.Mock \
  --startup-project src/backend/Integrations/Payments/Integrations.Payments.Mock --connection "<the Payments database>"
```

- Migrations are never applied at startup. The design-time factory refuses any declared environment (`ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`) other than Development or Staging.
- **Production:** the deployment pipeline must not apply this project's migrations. The production database never gets the `paymentsmock` schema, and the production hosts never compose the mock (startup refuses it).

## Symptoms and fixes
| Symptom | Cause | Fix |
|---|---|---|
| Checkout answers "try again", or attempts reach `ManualReview`, and the host logs "Mock payment state unusable for payment ..." (SQL error 208: the schema is missing; 50001: another call held the payment too long; none: the database is unreachable) | The `paymentsmock` schema is missing, or the database is unreachable | Apply the migration above. Then resolve the attempts in the admin payment review as usual. |
| Captures refused with "Unknown payment" after a database reset | The mock's payments were deleted, but the Payments module's attempts were not | Reset both schemas together, or resolve the attempts in review. |
| A test needs the old one-process behaviour | — | Set `Integrations:Payments:Mock:State=InProcess` (unit and contract tests only). |

## Never
- Never create the `paymentsmock` schema in a production database.
- Never point the mock at a database that holds real customer payments.
