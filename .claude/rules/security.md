# Security rules

Applies to all work. Background: ADR 0006, 0008, `docs/architecture/security.md`.

## Secrets and credentials
- No secrets in Git, `appsettings*.json`, test fixtures, docs, or client bundles. Use **user-secrets** locally and **Azure Key Vault + managed identity** in Azure.
- Provider credentials (supplier API keys, Stripe secret keys, webhook signing secrets) are server-side only, per environment, and rotatable. Stripe **publishable** keys are the only payment keys allowed in frontend config.
- Do not read `.env`, key, or certificate files unless the user explicitly asks. The secret-guard hook blocks writing them.

## Card data and payments
- **Raw card data (PAN, CVV, full track data) must never reach our servers, logs, database, or fixtures.** Card entry uses Stripe Elements/Checkout (PCI DSS SAQ-A scope). We store only provider tokens/IDs, brand, last4, and expiry month/year.
- Payment amounts come from the server-side order, never from the client.

## PII
- Classify data per `docs/architecture/security.md` (Public / Internal / PII / Sensitive PII / Payment).
- Never log names, emails, phone numbers, dates of birth, document numbers, or full addresses. Log IDs and redacted values. Supplier request/response capture must go through the redaction layer.
- Passport/ID document numbers and other Sensitive PII are encrypted at rest and access to them is audited.
- Honour retention rules: personal data is deletable or anonymisable, while financial records keep their legally required minimum.

## AuthN / AuthZ
- Every endpoint declares an authorization policy. **Deny by default**; anonymous access is explicit and limited to public search/content.
- Authorization is **permission-based** (e.g. `refunds.approve`), never checks on role names. Customers can only access their own orders (resource-level checks, no IDOR).
- Admin endpoints require staff identity with MFA and live in a separate route group with separate policies. High-risk admin actions (refund approval, markup changes, permission grants) are audited and support maker-checker.

## Input and transport
- Validate all input at the boundary: types, ranges, lengths, and formats (IATA codes, dates, currency codes, passenger counts).
- Use parameterised queries only (EF Core), with no string-concatenated SQL.
- Use secure headers (HSTS, CSP, `X-Content-Type-Options`, `frame-ancestors`) and an explicit CORS allow-list per environment, never `*` with credentials.
- Rate-limit search, auth-adjacent, and booking endpoints. Protect search from scraping, since supplier calls cost money.

## Audit and security events
- Audit log (append-only): actor, action, target, before/after (redacted), IP/user agent, correlation ID. It covers all admin actions and all payment/refund operations.
- Emit security events for authorization failures, suspicious booking velocity, and webhook signature failures.

## Tooling
- No production access from development tooling, Claude sessions, or MCP servers.
