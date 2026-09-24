## What & why

<!-- Story/issue link and a short description of the change. -->

## Type

- [ ] feat  - [ ] fix  - [ ] refactor  - [ ] docs  - [ ] test  - [ ] chore  - [ ] ci

## Architecture

- [ ] No new project, host, infrastructure, library or cloud service — **or** an ADR is linked: <!-- docs/adr/NNNN-… -->
- [ ] Module boundaries respected (only `*.Contracts` referenced across modules; no supplier DTOs outside `Integrations.*`)

## Booking / payment safety (tick N/A if untouched)

- [ ] N/A
- [ ] Commands are idempotent (idempotency key + unique constraint)
- [ ] No blind retries of supplier booking/ticketing or payment writes
- [ ] State changes go through the state machine and append to the timeline
- [ ] Prices are computed/revalidated server-side only
- [ ] Relevant rows in `docs/quality/failure-scenarios.md` have tests

## Security

- [ ] Every new endpoint declares an authorization policy
- [ ] No secrets, card data or unredacted PII in code, logs, fixtures or screenshots
- [ ] Admin actions are audited

## Verification (paste real output — no "should work")

```
<!-- dotnet test / npm test / playwright summary -->
```

## Docs

- [ ] `docs/progress.md` updated
- [ ] Architecture docs / runbooks / ADRs updated where behaviour changed
