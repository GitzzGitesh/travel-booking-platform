# 0012. Source control, branching, and CI

- **Status:** Proposed. Review strictness depends on team size (open question Q4).
- **Date:** 2026-09-24
- **Related:** `.claude/rules/workflow.md`, `.github/pull_request_template.md`, `docs/quality/definition-of-done.md`

## Context

The repository is on GitHub. We need a workflow that keeps `main` releasable, suits a small team plus AI-assisted development, and gives CI enough checks to catch boundary, contract, security, and test regressions.

## Decision

**Branching**: trunk-based development.
- `main` is protected: PR required, CI green required, linear history, no force pushes. At least one approving review once the team is larger than one person (Q4).
- Short-lived branches (ideally < 2 days): `feat/…`, `fix/…`, `chore/…`, `docs/…`, `refactor/…`, `test/…`, `ci/…`.
- **Squash merge.** The PR title becomes the commit message.
- Release via tags / deployment pipeline from `main`. Feature flags for incomplete features rather than long-lived branches.

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

**Repository settings to enable**: branch protection, secret scanning + push protection, Dependabot alerts and updates, CODEOWNERS once there are multiple owners.

## Consequences

**Positive:** small, reviewable changes; always-releasable `main`; regressions caught early.
**Negative:** CI time grows with Testcontainers and Playwright. Keep the PR suite lean and move the heavy suites to nightly.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| GitFlow (develop/release branches) | Long-lived branches and merge overhead with no benefit for continuous delivery |
| Azure DevOps Pipelines | The repository is on GitHub; Actions integrates with PRs and security features natively |
