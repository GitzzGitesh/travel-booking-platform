# 0013. OpenAPI TypeScript client generation

- **Status:** Proposed
- **Date:** 2026-09-25
- **Related:** [0003](0003-baseline-technology-stack.md), [0009](0009-frontend-applications-and-rendering.md), [0012](0012-source-control-branching-and-ci.md), `.claude/rules/api-design.md`, `.claude/rules/frontend-angular.md`

## Context

ADR 0009 requires the Angular apps to call the backend only through an `api-client` library **generated from OpenAPI**, but no generator was chosen. The contract is the committed snapshot `src/backend/Hosts/Api/openapi.v1.json`, an OpenAPI **3.1** document produced by ASP.NET Core 10. The generator must support 3.1, produce Angular `HttpClient` code (interceptors, SSR), have a permissive licence, add no runtime dependency, and produce deterministic output so CI can detect drift.

## Decision

- Use **`ng-openapi-gen`** (MIT, 1.x), pinned to an exact version as a frontend devDependency. It generates typed models, one function per operation, and an injectable `Api` helper on Angular `HttpClient`. There is no runtime package.
- The client lives in `src/frontend/projects/api-client/src`. It is **generated, committed, and never edited by hand**. Apps import it from source through the path alias `@travel-booking/api-client`, with no separate library build.
- Operation names come from explicit endpoint names (`.WithName(...)`), which the OpenAPI document exposes as `operationId`.
- CI (ADR 0012):
  - regenerates the client and fails if the committed code differs;
  - type-checks it;
  - runs **oasdiff** against the base branch's contract and fails on breaking changes. oasdiff was checked locally against OpenAPI 3.1 constraint and nullability changes (both caught). A missing base contract fails the check; it is never skipped.
- **Accepting an intentional breaking change** (for example, deleting the `Modules.Sample` spike, or a deprecation completed per `api-design.md`): add the specific change to a committed oasdiff ignore file passed with `--err-ignore`. Each accepted break is then one reviewed line in the PR diff. The file and its CI wiring are added the first time an override is needed.

## Consequences

**Positive:** typed end-to-end contract with no hand-written DTOs; contract changes are visible in PR diffs; breaking changes are caught before merge.
**Negative:** generated code is committed, so every contract change touches two places (snapshot and client). Mitigation: CI tells you exactly what to regenerate. An intentional breaking change needs an entry in the oasdiff ignore file (see Decision).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| `@hey-api/openapi-ts` | Still 0.x with frequent breaking changes |
| `orval` | Much larger dependency tree (27 direct dependencies) for features we don't need |
| `openapi-typescript` | Types only; HTTP calls would be hand-written, which ADR 0009 rules out |
| OpenAPI Generator (`typescript-angular`) | Requires a Java runtime in dev and CI |
| Generate at build time, not committed | Contract changes become invisible in PR review |
