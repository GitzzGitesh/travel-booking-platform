# 0028. customer-web customer sessions: a backend-for-frontend in the Api host

- **Status:** Accepted (2026-10-03) under the delegated decision authority of 2026-10-02
- **Date:** 2026-10-03
- **Deciders:** Engineering lead (delegated)
- **Related:** [0008](0008-identity-and-permissions.md) (decides its "frontend token handling" for `customer-web`), [0009](0009-frontend-applications-and-rendering.md), [0023](0023-admin-web-staff-session-bff.md) (the same pattern for staff), `docs/architecture/security.md`

## Context

Customers must sign in before booking (Q8). The customer API already accepts Entra External ID bearer tokens (the `Customer` scheme) and maps each account to our internal customer id. `customer-web` has no way to sign a customer in, so no booking step past the search can be built or tested end to end.

ADR 0008 left the `customer-web` token pattern open: either MSAL in the browser or a BFF, "evaluated against the same bar" as `admin-web`. The project owner deferred it until a tenant exists. No customer tenant exists yet (external), but the choice does not depend on the tenant, and every booking screen depends on the choice.

## Decision

1. **The same BFF pattern as staff (ADR 0023), in the existing Api host, owned by the Customers module.**
   - **`CustomerSignIn` (OpenID Connect):** authorization code with PKCE against the customer tenant, as a confidential client. Settings come from `Authentication:CustomerSession`: `Authority`, `ClientId`, `ClientSecret` (user-secrets or Key Vault only). Response mode `query`, `SaveTokens = false`, scope `openid` only. It is registered only once configured; until then sign-in answers 503 `customer-sign-in-unavailable` (fail closed). Its `Authority` must be the customer tenant's own authority (never `common`, `organizations` or `consumers`) and equal `Authentication:Customers:Authority`, so one person has one customer id; startup refuses anything else.
   - **`CustomerSession` (cookie `__Host-tb-customer`):** HttpOnly, Secure, `SameSite=Lax`, path `/`, not persistent. Idle 60 minutes, sliding; absolute 12 hours. Both are configurable.
     - `Lax` rather than `Strict`, so that a customer arriving from an email link or a payment provider's redirect is still signed in on a top-level GET. Unsafe requests are protected by the CSRF header (point 4), not by `SameSite`.
   - **A hint cookie, `tb-customer-hint=1`** (Secure, Lax, readable by script), set at sign-in and removed at sign-out or when a session is refused. customer-web asks `GET /session` only when it is present, so anonymous page views make no session request. It carries no credential, and the server never reads it.
2. **What the session holds:** the issuer, the account's object id and the sign-in time. It holds no token and no customer id, and it is protected by ASP.NET Data Protection.
   - **Every request** maps the account again through the same directory as bearer tokens. A customer has one internal id whether they use a token or the session.
   - **On sign-in:** reserved claims are refused, and so are app-only identities. No MFA is required of customers; the tenant's own policy applies.
3. **One customer identity.**
   - The `customer` policy accepts the session or a bearer token, never both on one request.
   - The bearer scope check (`RequiredScope`) applies to tokens only.
   - Endpoints open to anonymous callers that act for a signed-in customer (offer selection) use the session too. A refused session is never treated as anonymous.
4. **CSRF:** every unsafe request authenticated by the session must carry `X-TB-Customer-Csrf: 1`. Without it the request is unauthenticated and the session ends, recorded as a security event (`CustomerSessionRefused`, reason code only).
5. **Session endpoints (`/api/v1/session`):**
   - `GET /session` (`customer`): our customer id, for the UI only.
   - `GET /session/sign-in?returnUrl=` (anonymous, rate limited): local return paths only.
   - `POST /session/sign-out` (`customer`, CSRF header).
   - `POST /session/development-sign-in` (anonymous, CSRF header): a **Development-only** stand-in for the tenant, for local work and E2E. It is mapped only in Development with `Authentication:CustomerSession:DevelopmentSignIn` = `true`, and startup refuses the setting elsewhere. It uses the fixed issuer `urn:travel-booking:development-customer-sign-in`; a session with that issuer is refused outside Development.
6. **Hosting:** `customer-web` and the Api share an origin (the dev-server proxy locally, one ingress in deployment). Signed-in pages are rendered in the browser (ADR 0009), so the SSR server never forwards cookies or acts for a customer. A refused sign-in returns to `/?sign-in=failed`.

## Consequences

**Positive**
- No customer token is ever readable by script, so an XSS cannot take a credential away. Payment card entry stays in the provider's iframe (ADR 0006).
- The customer API is unchanged; bearer tokens stay available (for a future mobile app or tooling).
- It fails closed until the tenant exists, and the Development sign-in makes the whole journey testable now.

**Negative / trade-offs**
- **Data Protection keys** must be shared across Api instances (with the hosting story, Q2), as for staff. Until then a restart signs customers out.
- **Sign-out removes the browser's cookie only;** a copied cookie stays valid until it expires, as in ADR 0023. HttpOnly prevents script from copying it.
- **Every customer request maps the account** (one indexed lookup), as staff requests do.

**Follow-ups (external)**
- The customer tenant's app registration (redirect URI `/api/v1/session/callback`, a client credential in Key Vault).
- The Data Protection key ring and the production headers, with hosting (Q2).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| MSAL.js in `customer-web` (tokens in memory) | No server component, but customer tokens in the browser, refresh handling in an SSR app, and a second security model next to admin-web's |
| Wait for the tenant | Blocks every booking screen for a choice that does not depend on the tenant |
