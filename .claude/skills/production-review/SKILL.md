---
name: production-review
description: Production-readiness review checklist for a change, branch, or feature in this travel booking platform, covering correctness, booking/payment safety, security, data, observability, operability, tests, and docs. Use when asked to review a change, check whether something is ready to merge or ship, or assess production readiness.
---

# Production-readiness review

Review the target (current diff by default: `git diff main...HEAD` plus uncommitted changes). Read the changed files in full, not just the hunks. Report only real, specific findings.

## Delegate the deep dives
Run the reviewer agents in parallel when their area is touched, then merge their findings with yours:
- `booking-flow-reviewer`: orders, bookings, payments, refunds, pricing, providers, webhooks, jobs
- `security-reviewer`: endpoints, auth, PII, payments, admin, webhooks, config, logging
- `architecture-reviewer`: new projects/modules/hosts, cross-module references, DbContexts, migrations, provider adapters, frontend-to-API boundary, new dependencies

## Reviewer brief
The reviewer agents are read-only (Read, Grep, Glob). They **cannot run git**, so they only see the change you describe. Give every reviewer this brief in its prompt:

1. **Story**: the task/story ID and goal, in one or two sentences.
2. **Acceptance criteria**: the relevant criteria, verbatim where they exist.
3. **Changed files**: every added, modified, deleted, or renamed path, from `git status --porcelain` plus `git diff --name-status main...HEAD`. Untracked files do not appear in `git diff`, so list them explicitly.
4. **Diff**: `git diff main...HEAD` plus `git diff HEAD` for uncommitted work. If it is too large, include the diff for the files in the reviewer's area and say which files were left out. The reviewer reads the files in full itself.
5. **Context**: touched modules, the failure-scenario IDs from `docs/quality/failure-scenarios.md`, the relevant ADRs, and anything already known to be unfinished.

Send only what the reviewer needs. Never include secrets, real PII, or card data in the brief.

## Checklist

**Correctness and scope**
- [ ] Does what the story asks, and nothing unrelated
- [ ] Edge cases: empty/large inputs, multiple passengers/rooms, currencies with 0/3 decimals, time zones and DST

**Booking and payment safety**: see `.claude/rules/booking-and-payments.md`
- [ ] Server-side pricing and revalidation; idempotency enforced in the DB; no blind retries on writes; unknown outcome leads to reconcile
- [ ] State changes via the state machine, with timeline entries

**Security**: see `.claude/rules/security.md`
- [ ] Authorization policy on every endpoint; ownership checks; no secrets, card data, or PII in code/logs

**Data**
- [ ] Migration reviewed as SQL; expand/contract for breaking changes; indexes; concurrency tokens

**Observability and operability**
- [ ] Structured logs with correlation, order, and provider reference IDs (no PII)
- [ ] Traces span supplier/payment calls; metrics for new failure modes
- [ ] Alerts/runbooks for new stuck states or queues (`docs/runbooks/`)
- [ ] Admin/support can see and act on the new state (timeline, ops queue)

**Resilience**
- [ ] Explicit timeouts on outbound calls; circuit breakers; graceful degradation on provider outage
- [ ] Behaviour on process crash mid-flow is recoverable (outbox/Worker)

**API and frontend**
- [ ] No breaking OpenAPI change within a version; ProblemDetails types documented
- [ ] UI: no pricing logic, idempotency key per intent, accessible, i18n-ready

**Tests**: see `.claude/rules/testing.md`
- [ ] Tests exist at the right level for each touched failure scenario and state transition
- [ ] Tests were actually run, with output shown

**Docs**
- [ ] Architecture docs, failure-scenario catalog, runbooks, ADRs, and `docs/progress.md` updated

## Output
1. **Verdict**: Ready / Ready with follow-ups / Not ready.
2. **Blockers**: `file:line`, issue, scenario, fix.
3. **Should fix before production**.
4. **Follow-ups** (can be tracked separately).
5. **Not verified**: anything you could not check (e.g. tests not runnable).
