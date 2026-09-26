# Security architecture

**Status: Draft.** Related ADRs: 0006 (payments), 0008 (identity and permissions).

## Identity
| Audience | IdP | Requirements |
|---|---|---|
| Customers | Microsoft Entra External ID | Email/social sign-up, MFA optional (step-up for sensitive actions TBD), account recovery handled by the IdP |
| Staff | Microsoft Entra ID (workforce) | SSO, **MFA mandatory**, Conditional Access, no local accounts |

Tokens: the Api validates JWTs from both issuers with separate authentication schemes. Admin endpoints accept **only** the staff scheme. Frontend token handling (SPA vs BFF) is to be finalised in ADR 0008. The current lean is BFF-style cookies for `admin-web`.

## Authorization
- Permission-based: `orders.read`, `orders.read.pii`, `refunds.request`, `refunds.approve`, `pricing.markups.write`, `access.permissions.grant`, …
- Roles are bundles of permissions managed in the Access module. Code checks permissions, never role names.
- Customer endpoints enforce **resource ownership** (customer ID from the token, never from the request).
- Maker-checker for: refund approval above a threshold (Q11), markup and promotion changes, permission grants.
- Break-glass admin access: TBD (time-bound, fully audited).

## Data classification
| Class | Examples | Controls |
|---|---|---|
| Public | Airports, marketing content | None |
| Internal | Markups, supplier config, metrics | Staff auth |
| PII | Name, email, phone, DOB, address; the customer's identity-provider subject id (pseudonymous: stored as `CustomerId` on orders and payment attempts, and in the order timeline's actor) | Access by permission, never logged, retention rules. Erasure of the subject id in append-only rows is open: mapping it to an internal customer id is a precondition for the Orders endpoints (ADR 0008) |
| Sensitive PII | Passport/ID numbers, nationality + document expiry | **Encrypted at rest (application-level or Always Encrypted, per ADR)**, access audited, shortest retention |
| Payment | PaymentIntent IDs, last4, brand | No PAN/CVV ever. Tokens only |
| Secrets | Supplier keys, Stripe secret keys, webhook secrets | Key Vault only, managed identity, rotation |

## Threat outline (to be expanded into a full threat model before Phase 3)
| Threat | Mitigation |
|---|---|
| Price tampering | Server-side pricing from the persisted offer; client price ignored |
| IDOR on orders | Ownership checks; opaque IDs; tests for cross-customer access |
| Card testing / fraud | Stripe Radar, rate limits on checkout, velocity rules |
| Search scraping (supplier cost) | Rate limiting, bot protection (WAF), caching |
| Credential stuffing / account takeover | IdP protections, MFA, anomaly alerts |
| Webhook spoofing / replay | Signature verification, inbox dedupe, timestamp tolerance |
| Admin account compromise | MFA, Conditional Access, least privilege, maker-checker, audit, separate app and route group |
| Secret leakage | Key Vault, secret-guard hook, GitHub push protection, gitleaks in CI |
| PII leakage via logs/telemetry | Redaction layer, log review in PRs, no PII in exceptions |
| Supply-chain risk | Dependabot, lockfiles, licence checks, pinned SDKs |
| XSS / injection | Angular sanitisation, strict CSP, parameterised queries |

## Rate limiting and client addresses
- Every anonymous `/api/v1` endpoint has a per-client rate-limit policy (`BuildingBlocks.Http.RateLimitPolicies`): `anonymous` by default, and the tighter `supplier-calls` where a request reaches a paid supplier. Limits are in `RateLimiting` configuration and are per Api instance until a distributed limiter exists (ADR 0011).
- The client is the connection's address: per address for IPv4, per /64 prefix for IPv6 (one client controls a whole /64). It comes from `X-Forwarded-For` / `X-Forwarded-Proto` **only when the sender is a configured trusted proxy** (`ForwardedHeaders:KnownProxies/KnownNetworks`), and only the nearest hop counts. With nothing configured, forwarding is off. Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which trusts every sender.
- Each deployment sets its own ingress addresses (the hosting decision). Exposure outside Development stays gated until then (docs/progress.md).

## Platform controls (Azure, later phases)
Front Door + WAF · private endpoints for SQL and Key Vault · managed identities · Defender for Cloud · diagnostic logs to Log Analytics · separate subscriptions/resource groups per environment.
