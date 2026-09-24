# 0012. Source control, branching, and CI

- **Status:** Accepted. Q4 answered 2026-09-25 (see `docs/requirements/open-questions.md`); the review model below reflects it.
- **Date:** 2026-09-24 (revised 2026-09-25: review model for a one-developer team with a client/business release approver)
- **Related:** [0001](0001-record-architecture-decisions.md), `.claude/rules/workflow.md`, `.github/pull_request_template.md`, `docs/quality/definition-of-done.md`, `docs/requirements/open-questions.md` (Q4)

## Context

The repository is on GitHub. We need a workflow that keeps `main` releasable, suits a small team plus AI-assisted development, and gives CI enough checks to catch boundary, contract, security, and test regressions.

Team model (Q4, answered 2026-09-25): there is **one developer**. The **client/business owner reviews releases**. Nothing documents the client as a technical code reviewer, so this ADR does not assume they are one.

## Decision

**Branching**: trunk-based development.
- `main` is protected: changes land through PRs from short-lived branches. No direct pushes, no force pushes, no branch deletion, linear history, and the rules also apply to administrators (no bypass).
- CI status checks become **required** as soon as the CI workflow exists (Phase 1). Until then there is no check to require.
- The initial foundation commit was pushed directly to `main` to create the repository. It is the only exception. All later changes go through PRs.
- Short-lived branches (ideally < 2 days): `feat/…`, `fix/…`, `chore/…`, `docs/…`, `refactor/…`, `test/…`, `ci/…`.
- **Squash merge.** The PR title becomes the commit message.
- Release via tags / deployment pipeline from `main`. Feature flags for incomplete features rather than long-lived branches.

**Review model**: technical code review and business/release approval are separate and are not interchangeable.

| | Technical code review | Business / release approval |
|---|---|---|
| Who | The developer (currently the only technical reviewer), assisted by the `production-review` skill and the reviewer agents | The client/business owner |
| Covers | Correctness, `.claude/rules/`, tests and their output, security, the Definition of Done | Whether the release's scope and visible behaviour are acceptable to the business, and when it ships |
| When | Every PR, before merge | Every production release, before deployment |
| Record | The completed PR template (verification output, reviewer findings) | The approval recorded against the release tag. The mechanism is fixed together with the deployment pipeline |

- **While there is one developer, required PR approvals = 0.** GitHub does not let an author approve their own PR. A required approval would block every merge or push the developer into an administrator bypass, which weakens protection more than a zero count. Compensating controls: PRs are required, CI is required (from Phase 1), and every PR follows the Definition of Done and the PR template, with reviewer agents run for the areas they cover.
- Reviewer agents are **advisory**. They are not an approval and cannot approve or block merges (ADR 0001).
- The client/business owner is **not** a technical code reviewer unless that is documented later. Do not request code reviews from them or make them a CODEOWNER.
- **Access dependency:** if the client/business owner is expected to approve anything inside GitHub (a PR approval, or a deployment approval through a GitHub environment protection rule), they need their own GitHub account with the repository access that approval requires. Granting that access is a repository-access decision to record when it is made. Until then, their release approval is recorded outside GitHub and referenced from the release.
- **CODEOWNERS** is not used while there is one developer, because it would only request reviews from the author. Add it when there is a second technical owner.
- When a second developer joins: required approvals become 1 (a technical review), CODEOWNERS is added, and this ADR is revisited.

**Commits**: Conventional Commits (`type(scope): summary`), with the module or area as scope.

**PRs**: use the template (architecture, booking/payment safety, security, verification output, docs).

**CI (GitHub Actions)**, introduced in Phase 1:
| Check | When |
|---|---|
| .NET build (warnings as errors) + `dotnet format --verify-no-changes` | PR |
| Unit, architecture, integration (Testcontainers), API tests | PR |
| OpenAPI breaking-change diff (oasdiff) | PR |
| Frontend lint, unit tests, build | PR |
| Playwright smoke (mock providers) | PR |
| Secret scan (gitleaks) + GitHub secret scanning with push protection | PR / push |
| CodeQL (C#, TypeScript) | PR + weekly |
| Dependabot (NuGet, npm, Actions) | Weekly |
| Provider contract suites against supplier sandboxes, full E2E | Nightly |

**Repository settings to enable**: branch protection as described above (required approvals 0 while there is one developer), secret scanning + push protection, Dependabot alerts and updates, and CODEOWNERS once there is a second technical owner.

## Consequences

**Positive:** small, reviewable changes; always-releasable `main`; regressions caught early; business approval of releases without pretending it is a code review.
**Negative:** CI time grows with Testcontainers and Playwright. Keep the PR suite lean and move the heavy suites to nightly. With one developer there is no independent human code review. Required CI, the Definition of Done, and the reviewer agents reduce but do not remove that risk.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| GitFlow (develop/release branches) | Long-lived branches and merge overhead with no benefit for continuous delivery |
| Azure DevOps Pipelines | The repository is on GitHub; Actions integrates with PRs and security features natively |
