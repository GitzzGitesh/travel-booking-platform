# Workflow rules (scope, verification, git, documentation)

Applies to all work.

## Scope
- Do only what the current story asks. **Do not modify unrelated files**, reformat untouched code, or clean up beyond scope. Suggest follow-ups instead.
- Check `docs/progress.md` for the current phase. In the foundation phase, do not scaffold apps, add packages, or write application code.
- Ask before adding a dependency (NuGet/npm). Justify it and check its licence.

## Verification
- **Never claim something works without verifying it.** Build, run tests, or run the app, and report the real output. If verification was not possible, say exactly what was not verified.
- Report failures faithfully, with output. Don't paper over them.

## Git
- Trunk-based: `main` is always releasable and protected. Work on short-lived branches: `feat/<scope>-<desc>`, `fix/…`, `chore/…`, `docs/…`, `refactor/…`, `test/…`, `ci/…`.
- **Conventional Commits**: `type(scope): summary` (e.g. `feat(orders): add PendingConfirmation state`). The scope is the module or area.
- Keep commits and PRs small and focused. PRs are squash-merged after CI passes and review.
- Never force-push shared branches, rewrite `main` history, commit secrets, or skip hooks (`--no-verify`).
- Only commit or push when the user asks.

## Documentation
- Update docs **in the same change** as the behaviour: architecture docs, the failure-scenario catalog, runbooks, API docs.
- Update `docs/progress.md` at the end of each story (done, next, open questions).
- Architectural decisions go in a new ADR (`docs/adr/NNNN-title.md`, from `0000-template.md`). Accepted ADRs are superseded, not edited.
- Business or requirement questions go in `docs/requirements/open-questions.md`. Don't invent answers.
