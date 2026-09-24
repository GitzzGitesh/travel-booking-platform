# Progress

_Last updated: 2026-09-24_

## Current phase: 0 — Engineering foundation

No application code yet. **Do not scaffold applications, add dependencies, create databases/migrations, or write business code** until Phase 1 is approved.

### Done
- Architecture review and foundation proposal approved.
- Claude Code environment: `CLAUDE.md`, `.claude/rules/` (9), `.claude/skills/` (3), `.claude/agents/` (3), `.claude/settings.json`, and the secret-guard hook.
- Documentation structure: requirements, architecture drafts, ADRs 0001–0012 (**Proposed**), quality docs, runbook template.
- Repository hygiene: `.gitignore`, `.gitattributes`, `.editorconfig`, PR template.
- Reviewer-agent corrections: reviewers now receive a brief (story, acceptance criteria, changed files, diff); the Definition of Done requires `architecture-reviewer` for structural/boundary changes; the architecture reviewer checks the frontend → API boundary; the security reviewer checks error exposure and permission-matrix tests; the ADR exemption for the three initial reviewers is documented; and the failure-scenario ID scheme and count are documented in the catalog.

### Next (Phase 0 completion)
1. Review the foundation files, then make the initial commit on `main`.
2. Answer the blocking business questions in [`requirements/open-questions.md`](requirements/open-questions.md) (merchant of record, launch markets, first product, team size).
3. Review ADRs 0001–0012 and mark each Accepted or revise it.
4. GitHub settings: branch protection on `main`, secret scanning + push protection, Dependabot alerts.
5. Install tooling: Docker Desktop (or Podman) and the GitHub CLI (`gh`).
6. Fill the `TBD` values in [`requirements/non-functional.md`](requirements/non-functional.md).

## Roadmap (each phase needs explicit approval)

| Phase | Scope |
|---|---|
| 1 — Skeleton | `global.json`, backend solution (Api, Worker, Aspire AppHost, BuildingBlocks, architecture tests), CI workflow (build, test, format, OpenAPI diff, secret scan, CodeQL), Angular workspace skeleton (`customer-web`, `admin-web`), fill CLAUDE.md commands |
| 2 — Provider ports | `IFlightProvider` / `IHotelProvider` / `IPaymentProvider`, scenario-driven mock providers, provider contract suites; `add-provider-adapter` skill |
| 3 — First vertical slice | One product end-to-end with mocks: search → offer → revalidate → authorize → book → capture → confirm, with failure scenarios; `ef-migration` skill |
| 4 — Identity & admin foundation | External IdP integration, permissions, audit log, admin shell, booking timeline view |
| 5 — Real integrations | Stripe (test mode), first real supplier sandbox, reconciliation jobs |
| Later | Second product, refunds/cancellations UI, notifications, reporting, B2B |

## Open blockers
- Business decisions listed in [`requirements/open-questions.md`](requirements/open-questions.md) (Q1–Q4 block Phase 3 design).
- Docker not installed on the development machine (blocks integration tests from Phase 1).
