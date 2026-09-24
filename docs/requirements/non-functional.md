# Non-functional requirements

**Status: Draft.** Values marked `TBD` need a business decision. Don't invent them.

## Availability and reliability
| Item | Target |
|---|---|
| Customer site/API availability (monthly) | TBD (suggest 99.9%) |
| Admin portal availability | TBD (suggest 99.5%) |
| RPO (max data loss) | TBD (suggest ≤ 5 min, which Azure SQL PITR supports) |
| RTO (max downtime after disaster) | TBD (suggest ≤ 4 h) |
| Backup restore drill | TBD (suggest quarterly) |
| Supplier outage | Search degrades gracefully per provider. No booking attempted on an open circuit |

## Performance
| Item | Target |
|---|---|
| Flight search p95 (including supplier latency) | TBD. Supplier-bound, typically 3–10 s. Needs streaming or progressive results |
| Hotel search p95 | TBD |
| Non-search API p95 | TBD (suggest < 500 ms) |
| Customer web Core Web Vitals | LCP < 2.5 s, INP < 200 ms, CLS < 0.1 on landing pages |
| Peak concurrent searches | TBD |
| Supplier look-to-book limits | Per contract. Must be monitored |

## Security and compliance
- PCI DSS: **SAQ-A** scope (no card data on our systems). Depends on the merchant-of-record decision (Q1).
- Privacy: GDPR/UK GDPR (if EU/UK markets), CCPA (if US). Data subject requests, retention schedule, and data residency: TBD per Q2.
- Package travel / ATOL / seller-of-travel registrations: TBD per Q1 and Q2.
- Staff access: SSO + MFA required. Least privilege. Access reviews TBD.
- Audit log retention: TBD (suggest ≥ 7 years for financial actions, pending legal advice).
- Security testing: dependency and secret scanning on every PR, SAST (CodeQL), DAST baseline pre-release, penetration test before launch.

## Accessibility and reach
- WCAG 2.2 AA for customer and admin UIs.
- Languages at launch: TBD. Architecture is i18n-ready (including RTL) from day one.
- Display currencies at launch: TBD. Charge currencies: TBD (Q5).
- Time zones: flight/hotel times shown in local time with clear labels.
- SEO: SSR for public landing/content pages. Search results and booking pages are not indexed.

## Observability
- Distributed tracing across API, Worker, and supplier/payment calls (OpenTelemetry).
- Alerts: error rate, supplier failure rate, bookings stuck in `PendingConfirmation` > TBD minutes, outbox/webhook backlog, failed captures, reconciliation mismatches.
- Log retention: TBD. No PII in logs.

## Operability
- Zero-downtime deployments (expand/contract migrations).
- Feature flags for risky features and supplier enablement.
- Environments: local, dev, test/staging (supplier sandboxes, Stripe test mode), production.
