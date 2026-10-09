# Security architecture

**Status: Draft.** Related ADRs: 0006 (payments), 0008 (identity and permissions).

## Identity
| Audience | IdP | Requirements |
|---|---|---|
| Customers | Microsoft Entra External ID | Email/social sign-up, MFA optional (step-up for sensitive actions TBD), account recovery handled by the IdP |
| Staff | Microsoft Entra ID (workforce) | SSO, **MFA mandatory**, Conditional Access, no local accounts |

Tokens: the Api validates JWTs from both issuers with separate authentication schemes. Admin endpoints accept **only** the staff scheme. As built (ADR 0022):
- Staff tokens must show MFA (`amr`) and map to an internal staff id in the Access module.
- Admin routes (`/api/admin/v1`) require one `staff:{permission}` policy. Roles are permission bundles, granted by **maker-checker**: one administrator requests, a different one approves; nobody changes their own access. Grants and requests are audited. `Access:RoleAssignments` remains the bootstrap and break-glass (runbook `staff-access.md`): it is neither time-bound nor audited in-app (the platform's configuration change log traces it), and it is shown with its source in the grants list. The target of a change never decides on it, a revocation can only be withdrawn by its requester, and maker-checker refusals are `RoleChangeRefused` security events. Permissions are read on every sign-in, so revocation is immediate.
- Staff actions are audited (`AuditEntries` in the acting module's schema, in the same save).
- A 403 is the `StaffAuthorizationDenied` security event, and a refused staff token is `StaffTokenRefused`.
- Audit entries hold the client address (personal data), kept for the security audit period (12 months, Q9).
 `admin-web` uses a backend-for-frontend in the Api host (ADR 0023): sign-in runs on the server (OpenID Connect, code flow with PKCE), and the browser holds only an HttpOnly, Secure, SameSite=Strict session cookie (`__Host-tb-staff`), never a token.
  - The session holds the account and sign-in time only. Permissions are mapped again on every request, as for staff tokens.
  - Lifetimes: 30 minutes idle, 8 hours absolute.
  - Unsafe requests need the `X-TB-Staff-Csrf` header (a refusal is the `StaffTokenRefused` security event, reason `session-csrf-header-missing`).
  - A token and a session together are refused as ambiguous.
  - The Development-only sign-in stand-in is refused at startup in any other environment.
  - customer-web uses the same pattern as admin-web (ADR 0028): an HttpOnly session cookie (`__Host-tb-customer`, SameSite=Lax, idle 60 minutes, at most 12 hours) set by the Api after an OpenID Connect sign-in; no token in the browser; the CSRF header `X-TB-Customer-Csrf` on unsafe requests.

## Authorization
- Permission-based: `orders.read`, `orders.read.pii`, `refunds.request`, `refunds.approve`, `pricing.markups.write`, `access.permissions.grant`, …
- Roles are bundles of permissions managed in the Access module. Code checks permissions, never role names.
- Customer endpoints enforce **resource ownership** (customer ID from the token, never from the request).
- Maker-checker for: refund approval above a threshold (Q11), markup and promotion changes, permission grants.
- Break-glass admin access: today `Access:RoleAssignments` (configuration), which is neither time-bound nor audited in-app; making it time-bound and audited is a follow-up (runbook `staff-access.md`).

## Data classification
| Class | Examples | Controls |
|---|---|---|
| Public | Airports, marketing content | None |
| Internal | Markups, supplier config, metrics | Staff auth |
| PII | Traveller names, date of birth and gender, booker email and phone (Q9: nothing else in the MVP, no special-category data), kept only in the Customers personal-data store (ADR 0020) and anonymised 25 months after the last flight, or 30 days after an order is abandoned unbooked (its documents at once), unless on legal hold; address only if a supplier or tax rule requires it; the customer's identity-provider issuer and user object id (`oid`, pseudonymous), kept **only** in `customers.Customers`, where they map to our internal customer id | Access by permission, never logged, retention rules. Orders, payment attempts and timelines store the **internal** customer id, never the subject, so erasing a customer means deleting one mapping row (retention: Q9) |
| Sensitive PII | Passport/ID numbers, nationality + document expiry | Collected only when the supplier requires it. **Encrypted per document** (AES-256-GCM envelope, key-encryption key from user-secrets / Key Vault; ADR 0020), never returned by the API or logged, every store/read/shred audited in `customers.DocumentAccessLog`, crypto-shredded 30 days after the last flight |
| Payment | PaymentIntent IDs, last4, brand | No PAN/CVV ever. Tokens only |
| Secrets | Supplier keys, Stripe secret keys, webhook secrets | Key Vault only, managed identity, rotation |

## Threat outline (to be expanded into a full threat model before Phase 3)
| Threat | Mitigation |
|---|---|
| Price tampering | Server-side pricing from the persisted offer; client price ignored |
| IDOR on orders | Ownership checks; opaque IDs; tests for cross-customer access |
| Card testing / fraud | Stripe Radar (the fraud engine, Q10) with 3-D Secure per its risk policy; payment attempt limits (`Payments:AttemptLimits`: 5 per order, 10 per customer in 24 hours), repeated refusals raising `PaymentAttemptLimitRepeated` and a review list (no automatic block in the MVP); per-address rate limits before authentication, and a per-customer write limit (`RateLimiting:Customer`) on customer order and traveller endpoints |
| Search scraping (supplier cost) | Rate limiting, bot protection (WAF), caching |
| Credential stuffing / account takeover | IdP protections, MFA, anomaly alerts |
| Webhook spoofing / replay | Signature verification, inbox dedupe, timestamp tolerance |
| Admin account compromise | MFA, Conditional Access, least privilege, maker-checker, audit, separate app and route group |
| Secret leakage | Key Vault, secret-guard hook, GitHub push protection, gitleaks in CI |
| PII leakage via logs/telemetry | Redaction layer, log review in PRs, no PII in exceptions |
| Supply-chain risk | Dependabot, lockfiles, licence checks, pinned SDKs |
| XSS / injection | Angular sanitisation, strict CSP, parameterised queries |

## Rate limiting and client addresses
- Every anonymous `/api/v1` endpoint has a per-client rate-limit policy (`BuildingBlocks.Http.RateLimitPolicies`): `anonymous` by default, and the tighter `supplier-calls` where a request reaches a paid supplier. Payment provider notifications (webhooks) have their own budget, `payment-notifications`, so a provider's burst never competes with customers and a flood is cut before any signature check. Limits are in `RateLimiting` configuration and are per Api instance until a distributed limiter exists (ADR 0011).
- The client is the connection's address: per address for IPv4, per /64 prefix for IPv6 (one client controls a whole /64). It comes from `X-Forwarded-For` / `X-Forwarded-Proto` **only when the sender is a configured trusted proxy** (`ForwardedHeaders:KnownProxies/KnownNetworks`), and only the nearest hop counts. With nothing configured, forwarding is off. Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which trusts every sender.
- Each deployment sets its own ingress addresses (the hosting decision). Exposure outside Development stays gated until then (docs/progress.md).

## Platform controls (Azure, later phases)
Front Door + WAF · private endpoints for SQL and Key Vault · managed identities · Defender for Cloud · diagnostic logs to Log Analytics · separate subscriptions/resource groups per environment.
