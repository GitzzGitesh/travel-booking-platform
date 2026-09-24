# 0009. Frontend applications and rendering

- **Status:** Proposed
- **Date:** 2026-09-24
- **Related:** [0003](0003-baseline-technology-stack.md), [0008](0008-identity-and-permissions.md), `.claude/rules/frontend-angular.md`

## Context

Customers and staff have different needs. The customer site needs SEO for landing/content pages, fast first load, public caching, and broad i18n. The admin portal needs staff SSO, a strict CSP, no indexing, dense data UIs, and a separate security surface. Combining them in one app mixes auth models and increases the attack surface of the admin portal.

## Decision

- One **Angular CLI workspace** (no Nx) under `src/frontend/` containing:
  - `customer-web`: **SSR / hybrid rendering** (prerender or SSR for public pages, client rendering for authenticated and booking flows).
  - `admin-web`: **SPA**, no SSR, deployed separately with a strict CSP and its own domain.
  - Shared libs: `api-client` (**generated from OpenAPI**), `ui` (shared components/design tokens), `i18n` utilities.
- The apps never import each other. Sharing happens only through libs.
- Standalone components, signals, strict TypeScript. Angular's default unit test runner (Vitest), Playwright for E2E.
- The i18n library choice (Angular built-in i18n vs runtime translation) will be decided in a later ADR once languages are known (Q2).

## Consequences

**Positive:** right rendering model per audience; admin isolated from the public attack surface; one toolchain and shared code.
**Negative:** two deployments; SSR adds a Node runtime for `customer-web` hosting; SSR-safe coding discipline is required.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Single Angular app for both | Mixed auth/CSP/SEO needs; larger admin attack surface |
| Nx monorepo | Extra tooling not needed for two apps and a few libs |
| Separate repositories | Duplicated tooling; harder to keep the API client in sync |
| No SSR | Weak SEO for landing/content pages |
