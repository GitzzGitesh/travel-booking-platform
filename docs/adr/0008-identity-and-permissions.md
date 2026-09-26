# 0008. Identity and permissions

- **Status:** Proposed. **Q8 answered 2026-09-26: sign-in is required before booking (no guest checkout)**, so the guest-checkout clause below will not apply. Acceptance still needs the identity-provider tenants and configuration, and the Phase 4 decision on how the frontends handle tokens.
- **Date:** 2026-09-24
- **Related:** `docs/architecture/security.md`, `.claude/rules/security.md`, open question Q8

## Context

We need customer sign-up/sign-in (possibly guest checkout), staff sign-in with MFA, and fine-grained admin authorization (refunds, pricing, permissions). Building identity (password storage, MFA, recovery, breach detection) ourselves is costly and a major security liability. The platform targets Azure.

## Decision

- **Customers**: Microsoft Entra External ID (OIDC).
- **Staff**: Microsoft Entra ID (workforce tenant), with **MFA and Conditional Access mandatory**.
- The Api validates tokens from both with **separate authentication schemes**. Admin routes (`/api/admin/*`) accept only the staff scheme.
- **Authorization stays in our application**: permission-based policies (`refunds.approve`, …), with roles as bundles of permissions managed in the Access module. Code never checks role names.
- Every endpoint declares a policy (deny by default). Architecture/API tests enforce this.
- Customer data access is always scoped by the authenticated customer ID (resource ownership).
- Frontend token handling: **to be finalised at the Phase 4 scaffold**. The current lean is a BFF/cookie pattern for `admin-web` (no tokens in browser storage), with standard MSAL for `customer-web` evaluated against the same bar.
- Guest checkout (Q8), if accepted, uses a booking reference + email + one-time code to retrieve bookings.

## Consequences

**Positive:** MFA, recovery, and threat detection come from the IdP; staff lifecycle ties to corporate identity; clear permission model.
**Negative:** vendor coupling to Microsoft identity; External ID customisation limits for branded flows; two issuers to configure and test.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| ASP.NET Core Identity (+ OpenIddict) | We would own MFA, recovery, and breach handling. More risk and work |
| Auth0 / Okta | Strong options; a cost and Azure-alignment trade-off. Revisit if External ID proves limiting |
| Role checks in code | Brittle; can't express maker-checker or fine-grained ops permissions |
