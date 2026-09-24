# Testing rules

Applies to all work. Background: `docs/quality/testing-strategy.md`, `docs/quality/failure-scenarios.md`.

- Test levels and locations follow `docs/quality/testing-strategy.md`: domain unit, application, architecture, integration (Testcontainers SQL Server), API (`WebApplicationFactory`), provider contract suites, concurrency, frontend unit, Playwright E2E.
- Every **state transition** (legal and illegal) of Order/Booking/Payment/Refund has a unit test.
- Every scenario in `docs/quality/failure-scenarios.md` that a change touches must have a test at the listed level before the story is done. Update the catalog's status column.
- Mock providers are deterministic and scenario-driven. Tests select scenarios explicitly and never depend on randomness or wall-clock time. Control time with `FakeTimeProvider`.
- Every provider implementation, **including mocks**, must pass the shared provider contract suite for its port.
- Concurrency-sensitive commands (book, capture, refund, cancel) have a test that fires duplicate/parallel requests and asserts exactly one effect.
- Integration tests use a real SQL Server container, not the EF InMemory provider or SQLite.
- Authorization: API tests cover the permission matrix (allowed and forbidden) for every endpoint, including cross-customer access attempts.
- Never delete, skip, or weaken a test to make a build pass. If a test is wrong, explain why and fix it explicitly.
- **Before declaring anything done, run the relevant tests and include the actual output summary.** If tests could not be run, say so plainly.
- Test data contains no real PII, real card numbers, or real credentials. Use Stripe test cards and synthetic travellers.
