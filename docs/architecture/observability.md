# Observability

**Status: Draft.** Backend choice (Application Insights via the Azure Monitor OpenTelemetry distro) is to be confirmed in a later ADR.

## Signals
- **Traces**: OpenTelemetry across `customer-web`/`admin-web` → Api → Worker (the outbox carries trace context) → supplier/Stripe calls → SQL.
- **Logs**: structured `ILogger`, correlated by trace ID. **No PII.** Include `OrderId`, `OrderItemId`, `PaymentId`, `ProviderId`, and `ClientReference` as structured properties.
- **Metrics**: technical (request rate, errors, latency, dependency health) and **business** metrics.

## Business metrics (initial)
| Metric | Why |
|---|---|
| Searches, offers returned, and search latency per provider | Supplier performance, look-to-book |
| Revalidation outcomes (ok / price changed / expired) | Offer freshness, UX |
| Bookings by outcome (confirmed / failed / pending) per provider | Core health |
| `PendingConfirmation` count and age | Stuck bookings, the key operational risk |
| Authorizations voided, captures failed | Payment health |
| Refunds requested / succeeded / failed | Customer impact |
| Webhook inbox lag, outbox lag | Async health |

## Alerts (initial)
| Alert | Condition (tune later) | Runbook |
|---|---|---|
| Booking stuck | Any `PendingConfirmation` older than TBD min | `runbooks/booking-pending-confirmation.md` (planned) |
| Capture failures | Any `CaptureFailed` → `ManualReview` | `runbooks/payment-captured-booking-failed.md` (planned) |
| Supplier degraded | Error rate > TBD% or circuit open | `runbooks/provider-outage.md` (planned) |
| Webhook backlog | Inbox lag > TBD min | `runbooks/webhook-backlog.md` (planned) |
| Outbox backlog | Oldest undispatched > TBD min | (planned) |
| API errors | 5xx rate > TBD% | (planned) |
| Security | Webhook signature failures, authorization-failure spikes | (planned) |

## Support tooling
The admin **booking timeline** is the primary support tool: support should rarely need raw logs. Each timeline entry links its trace ID for engineering escalation.

## Local development
The .NET Aspire dashboard (if ADR 0003's Aspire option is accepted) shows traces, logs, and metrics locally with no cloud dependency.
