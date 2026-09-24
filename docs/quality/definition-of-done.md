# Definition of Done

A story is done only when **all** applicable items are true. "It should work" is not done.

Reviewer agents are required only by the sections below that apply to the change. A trivial change (a docs typo, a comment, or a formatting fix in one file) may need none. When a reviewer runs, brief it with the story, acceptance criteria, changed files, and diff (see **Reviewer brief** in `.claude/skills/production-review/SKILL.md`).

## Every story
- [ ] Acceptance criteria met. Nothing outside the story's scope changed.
- [ ] Code follows `.claude/rules/`. No boundary violations and no new dependencies/infrastructure without an ADR.
- [ ] Build passes with warnings as errors. Format and lint checks pass.
- [ ] Relevant tests were added and **run, with actual output recorded** in the PR.
- [ ] No secrets, card data, or PII in code, logs, fixtures, or screenshots.
- [ ] `docs/progress.md` updated.
- [ ] PR follows the template. Conventional Commit title. CI green.

## When the story touches bookings, payments, refunds, providers, or async flows
- [ ] Relevant rows in `failure-scenarios.md` have tests at the listed levels, and their status is updated.
- [ ] State transitions go through aggregates and write timeline entries.
- [ ] Idempotency is enforced by a DB constraint. No retries on non-idempotent writes.
- [ ] `booking-flow-reviewer` has run, and its blockers are resolved.
- [ ] Architecture lifecycle docs are updated if states or flows changed.

## When the story touches endpoints, auth, PII, admin, webhooks, or configuration
- [ ] Authorization policy and permission-matrix tests exist.
- [ ] `security-reviewer` has run, and its blockers are resolved.
- [ ] OpenAPI changes are non-breaking (or versioned), and the generated client is updated.

## When the story changes structure or boundaries
Applies to new projects, modules, or hosts; changes to `*.Contracts` or cross-module references; new DbContexts or migrations; provider adapters (`Integrations.*`); frontend API access; and new dependencies.
- [ ] Architecture tests cover any new boundary.
- [ ] `architecture-reviewer` has run, and its blockers are resolved.

## When the story touches the database
- [ ] The migration SQL script was reviewed (data loss, locking, indexes). Expand/contract is used for breaking changes.
- [ ] Integration tests run against the SQL Server container.

## When the story adds an operational failure mode
- [ ] Metrics/alerts added and a runbook written in `docs/runbooks/`.
- [ ] Admin/support can see the state (timeline or ops queue).

## When the story changes the UI
- [ ] Verified in a browser (Playwright or manual), with what was checked stated.
- [ ] Accessible (keyboard, labels, contrast) and i18n-ready.
