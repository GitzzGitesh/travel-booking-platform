# Progress

_Last updated: 2026-09-25_

## Current phase: 0 — Engineering and architecture foundation (closing, not yet complete)

No application code yet. **Do not scaffold applications, add dependencies, create databases/migrations, or write business code** until Phase 1 is approved.

### Done
- Architecture review and foundation proposal approved.
- Claude Code environment: `CLAUDE.md`, `.claude/rules/` (9), `.claude/skills/` (3), `.claude/agents/` (3), `.claude/settings.json`, and the secret-guard hook.
- Documentation structure: requirements, architecture drafts, ADRs 0001–0012, quality docs, runbook template.
- Repository hygiene: `.gitignore`, `.gitattributes`, `.editorconfig`, PR template.
- Initial commit `dbeed7b` (`chore: establish engineering foundation`) pushed to `origin/main` on GitHub.
- GitHub repository security settings configured: branch protection on `main`, secret scanning + push protection, Dependabot alerts (confirmed by the developer on 2026-09-25; not independently verified from tooling).
- Q4 answered (one developer; the client/business owner reviews releases). Recorded in [`requirements/open-questions.md`](requirements/open-questions.md), with the review model in ADR 0012.
- Phase 0 exit criteria and phase-entry gates documented (below). ADRs 0001, 0010, and 0012 revised.
- Phase 1 ADR decision (2026-09-25): **Accepted** 0001, 0002, 0007, 0009, 0010, 0012. **ADR 0003 stays Proposed** until the Phase 1 plan deliberately decides its four open choices: minimal APIs vs controllers, validation library, assertion library, and architecture-test library. ADRs 0004, 0005, 0006, 0008, and 0011 remain Proposed.
- Reviewer-agent corrections: reviewers now receive a brief (story, acceptance criteria, changed files, diff); the Definition of Done requires `architecture-reviewer` for structural/boundary changes; the architecture reviewer checks the frontend → API boundary; the security reviewer checks error exposure and permission-matrix tests; the ADR exemption for the three initial reviewers is documented; and the failure-scenario ID scheme and count are documented in the catalog.

### Phase 0 exit criteria
Phase 0 closes when **all** of these are true. Q1, Q2, and Q3 do **not** block Phase 0; they are phase-entry gates (below).

| # | Criterion | Status |
|---|---|---|
| 1 | Engineering foundation complete (docs, rules, quality docs, repository hygiene) | Done |
| 2 | GitHub repository established (initial commit pushed) | Done |
| 3 | Repository security/protection configured (branch protection on `main`, secret scanning + push protection, Dependabot alerts) | Done (developer-confirmed) |
| 4 | Claude Code engineering environment established (`CLAUDE.md`, rules, skills, reviewer agents, secret-guard hook) | Done |
| 5 | Q4 answered | Done |
| 6 | Phase 1 architectural ADRs accepted or revised: 0001, 0002, 0003, 0007, 0009, 0010, 0012 | Done. Accepted: 0001, 0002, 0007, 0009, 0010, 0012. **0003 deliberately kept Proposed** (its four choices are decided in the Phase 1 plan) |
| 7 | Explicit Phase 0 exit criteria documented | Done (this section) |
| 8 | This documentation/ADR update reviewed and committed through the PR workflow | **Open** |

### Next (Phase 0 completion)
1. Review and commit this documentation/ADR update (criterion 8).
2. In the Phase 1 plan: decide ADR 0003's four open choices (minimal APIs vs controllers, validation library, assertion library, architecture-test library), then accept or revise ADR 0003 before scaffolding.
3. Start the business answers for Q1, Q3, Q6, Q2, and Q5 early. They gate later phases, not Phase 0.

## Phase-entry gates
Business questions that must be answered before specific later work. A gate is **not** an answer: each question stays Open in [`requirements/open-questions.md`](requirements/open-questions.md) until the business decides it.

| Question | Must be answered before | Architecture it unblocks |
|---|---|---|
| Q1 Merchant of record | Payment architecture is finalised; Phase 2 payment work (`IPaymentProvider`) | ADR 0005, ADR 0006 |
| Q3 First product | Implementation priority is finalised; the Phase 2 product slice (which provider port and mock come first) | ADR 0004 (first port), Phase 3 slice |
| Q6 Target suppliers | The supplier provider ports are frozen; Phase 5 | ADR 0004, per-supplier ADRs |
| Q2 Launch markets | Production-market, compliance, and hosting-region decisions; production-oriented design in Phase 3 | Compliance scope, data residency, i18n ADR (per ADR 0009) |
| Q5 Charge currencies and FX policy | Real payment/currency integration (Phase 5) | Separate FX-policy ADR (ADR 0010 leaves it out of scope) |

Q7–Q12 remain tied to the later phases listed in [`requirements/open-questions.md`](requirements/open-questions.md).

## Deferred prerequisites
- **Docker Desktop or Podman** (for Testcontainers) is not a Phase 0 requirement. It becomes required when integration-test infrastructure is introduced in Phase 1 (ADR 0003). Not installed yet.
- **GitHub CLI (`gh`)**: optional convenience for PR work; not required.
- **`TBD` values** in [`requirements/non-functional.md`](requirements/non-functional.md) (availability, RPO/RTO, performance, retention): needed before the first production deployment, not to close Phase 0.

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
- **Phase 0:** exit criterion 8 (review and commit of this documentation/ADR update).
- **Later phases:** the phase-entry gates above (Q1, Q3, Q6, Q2, Q5).
