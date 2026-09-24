---
paths:
  - "**/Persistence/**"
  - "**/Migrations/**"
  - "**/*DbContext*.cs"
  - "**/*.sql"
---
# Database rules

- SQL Server / Azure SQL via EF Core. **One schema per module** (`orders`, `payments`, …). A module never reads or writes another module's schema.
- Aggregates have a `rowversion` concurrency token. Handle `DbUpdateConcurrencyException` explicitly.
- Idempotency is enforced by **unique constraints** (e.g. `(Scope, IdempotencyKey)`, `(Provider, ProviderEventId)` for webhooks). Don't rely on check-then-insert.
- Money columns: `decimal(19,4)` + `char(3)` currency. Instants: `datetimeoffset`. Local travel times: `datetime2` + IANA zone / airport code.
- Status columns store the state machine's enum as a string (readable in ops queries), with a check constraint where practical.
- Timeline, audit, outbox, and inbox tables are **append-only** from application code (no UPDATE/DELETE, apart from outbox dispatch markers).
- Sensitive PII columns (document numbers) are encrypted (see `docs/architecture/security.md`). Never add a plaintext copy for convenience.
- Migrations:
  - Generate with EF, then **review the generated SQL script** (`dotnet ef migrations script`) for data loss, locking, and index impact before committing.
  - Breaking changes use **expand → migrate → contract** across releases. Never drop or rename a column in the same release that stops using it.
  - Migrations are applied by the deployment pipeline, not on app startup in production.
  - Never run `dotnet ef database update` against a non-local database from a dev machine or Claude session.
- Index every foreign key and known lookup path (provider reference, idempotency key, customer ID + created date).
