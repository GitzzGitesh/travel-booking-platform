# 0008. Identity and permissions

- **Status:** Proposed. **Q8 answered 2026-09-26: sign-in is required before booking (no guest checkout)**, so the guest-checkout clause below will not apply. Acceptance still needs the identity-provider tenants and configuration, and the Phase 4 decision on how the frontends handle tokens.
- **Implemented as directed (2026-09-28), customer side only, without a tenant:**
  - the `Customer` scheme: JwtBearer (package approved 2026-09-28), validating signature, issuer, audience and lifetime, RS256 only. Tenant values come from `Authentication:Customers` (`Authority`, `Audience`, optional `RequiredScope`), which are placeholders, so every customer token is refused until a tenant is configured;
  - the `customer` policy, and the account mapped to an internal customer id in the Customers module. The account key is the token's issuer plus the user's object id (`oid`), which Entra keeps stable across a tenant's applications, unlike the per-application `sub`. It is compared exactly (binary collation, printable ASCII). No other module sees it;
  - our id lives in a separate identity that only the Customers module creates. A token carrying the reserved claim in any spelling is refused, and so is an app-only token (`idtyp` app);
  - once `Authority` is set, `RequiredScope` is required at startup, and the policy demands that delegated scope;
  - customer authentication runs only where a customer policy asks for it (the automatic default scheme is suppressed), and a refused token's reason is not returned;
  - customer endpoints act on the caller's own resources only.
- **Not done:** the staff scheme (Entra ID), the Access module and permissions (no admin endpoints yet). The customer-web token pattern (MSAL or BFF) was deferred by the project owner until a tenant exists, and customer-web has a signed-out session boundary only. There is no fallback authorization policy: ASP.NET would also apply it to unmatched routes (401 instead of 404), so deny by default stays enforced by `EndpointAuthorizationTests`.
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
