---
name: implement-story
description: Delivery workflow for implementing a user story, task, or bug fix in this repository, from reading context and planning through failure-path tests, implementation, verification with real output, docs/progress updates, and review. Use whenever asked to implement, build, add, or fix a feature or story.
---

# Implement a story

Follow these steps in order. Do not skip verification.

## 1. Understand
- Read `docs/progress.md` (current phase and story). If the request conflicts with the current phase (e.g. scaffolding during the foundation phase), stop and say so.
- Restate the story's goal and acceptance criteria in 2–5 bullets. If acceptance criteria are missing or ambiguous in ways that change the design, ask.
- Identify the affected modules and read the relevant ADRs (`docs/adr/`), architecture docs, and rules (`.claude/rules/`).
- For booking/payment/provider work, consult `docs/quality/failure-scenarios.md` and list the scenarios this story touches. Use the `travel-domain` skill for background.

## 2. Plan
- List the files you expect to create or change. Anything outside the story's scope needs a reason.
- If the plan needs a new project, library, host, infrastructure, or an architecture deviation, stop and raise an **ARCHITECTURE REVIEW** (format in CLAUDE.md) before proceeding.
- For non-trivial stories, present the plan and wait for approval.

## 3. Tests first for the risky parts
- Write tests for state transitions, idempotency, and each touched failure scenario before (or alongside) the implementation, at the level given in `docs/quality/testing-strategy.md`.
- Use mock providers' explicit scenarios and `FakeTimeProvider`; never rely on real time or randomness.

## 4. Implement
- Follow the rules. Keep endpoints thin, keep supplier types in `Integrations.*`, route state changes through aggregates, and use the outbox for side effects.
- Keep the diff minimal and focused. Don't reformat or refactor unrelated code.

## 5. Verify (mandatory)
- Build, and run the relevant test projects/specs. Once the scaffold exists, the commands are in CLAUDE.md.
- Paste the **actual** result summary (passed/failed counts, failures). If something could not be run, say exactly what and why.
- For UI changes, exercise the flow in a browser (Playwright/e2e or manual via the run skill) and say what was checked.

## 6. Document
- Update `docs/quality/failure-scenarios.md` status for covered scenarios.
- Update the architecture docs, runbooks, or API docs whose described behaviour changed.
- Update `docs/progress.md`: what was done, what's next, new open questions.

## 7. Review
Run only the reviewers whose area the change touches (see `docs/quality/definition-of-done.md`). A trivial change may need none.
- For changes touching bookings, payments, refunds, providers, or async flows, run the `booking-flow-reviewer` agent.
- For endpoints, auth, PII, admin, webhooks, or config, run the `security-reviewer` agent.
- For new modules/projects/hosts, cross-module or boundary changes, migrations, provider adapters, or frontend API access, run the `architecture-reviewer` agent.
- Reviewers are read-only and cannot run git. Brief each one with the story, the acceptance criteria from step 1, the changed-file list, and the diff, following **Reviewer brief** in `.claude/skills/production-review/SKILL.md`.
- Address blockers, and list any remaining findings for the user.

## 8. Report
Summarise: what changed (files), how it was verified (with output), what reviewers found and how it was handled, and follow-ups. Do not commit unless asked. When asked, use Conventional Commits.
