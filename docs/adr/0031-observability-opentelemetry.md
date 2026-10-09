# 0031. Observability: OpenTelemetry, exported to Azure Monitor or OTLP

- **Status:** Accepted (2026-10-09) under the delegated decision authority
- **Date:** 2026-10-09
- **Deciders:** Engineering lead (delegated)
- **Related:** [0003](0003-baseline-technology-stack.md) (OpenTelemetry SDK → Azure Monitor), [0007](0007-async-processing-worker-and-outbox.md), `docs/architecture/observability.md`, `docs/architecture/security.md`

## Context

ADR 0003 named OpenTelemetry with Azure Monitor as the observability stack, but nothing was wired. Both hosts logged to
the console only. The outbox already starts spans (`TravelBooking.Outbox`) that nobody collected. Operations have alert
event names (for example `BookingUnresolved`, `HotelCancellationShortfall`), but no metric for booking health. There is
no hosting decision yet, so the export target cannot be assumed.

## Decision

We will:

1. **Instrument both hosts with OpenTelemetry**, through one extension, `AddPlatformTelemetry`. It is one source file (`src/backend/Hosts/Shared`) compiled into both hosts, so BuildingBlocks stays framework-only and no new project is needed.
   - **Resource:** service name (`travel-booking-api` or `travel-booking-worker`), version and environment.
   - **Traces:** ASP.NET Core (health probes excluded), HttpClient (supplier and Stripe calls), and every `TravelBooking.*` activity source.
   - **Metrics:** ASP.NET Core, HttpClient, the .NET runtime, and every `TravelBooking.*` meter.
   - **Logs:** the existing `ILogger` output, through OpenTelemetry, correlated by trace id.
2. **Export only where configured.**
   - To Azure Monitor (Application Insights) when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set. It comes from Key Vault or the host's environment, never from appsettings.
   - To OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set: a collector, or locally the Aspire dashboard container.
   - Otherwise nothing is exported. Production must set one of them; the deployment ADR will enforce it.
3. **Never export personal data.**
   - URL query values are redacted (the instrumentation's default, kept).
   - SQL statements are not captured: there is no SqlClient or EF instrumentation yet, and EF Core's own logs (which print each command's SQL at Information) are not exported below Warning.
   - No request or response bodies.
   - Logs keep their existing rule: ids only.
   - Metric tags are low-cardinality facts (product, outcome), never ids.
4. **Business metrics start with booking health.** `travelbooking.orders.booking_outcomes` (meter `TravelBooking.Orders`, tags `product` and `outcome`) counts each order item that reaches Confirmed, Failed, PendingConfirmation or ManualReview. It is counted once, when the outcome is saved. The other business metrics in `observability.md` follow the same pattern as they become needed.

## Consequences

**Positive**
- Traces, metrics and logs work the same in both hosts. Local runs can show them in the Aspire dashboard without a cloud account.
- Outbox spans and supplier calls are visible; alert event names now come with a booking-outcome metric.
- Exporting is a configuration change, so the hosting decision is not needed now.

**Negative / trade-offs**
- Six new packages (OpenTelemetry under Apache-2.0, the Azure Monitor exporter under MIT), plus the in-memory exporter for tests.
- No SQL dependency spans until an instrumentation that cannot capture parameter values is added.

**Follow-ups**
- The deployment ADR sets the Application Insights connection string per environment, and fails startup in Production without an exporter.
- More business metrics as needed: searches, revalidation outcomes, captures and voids, refunds, outbox and inbox lag.
- SQL dependency spans with statements disabled.
- Browser telemetry for customer-web and admin-web.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Azure Monitor OpenTelemetry distro (`UseAzureMonitor`) | It is ASP.NET Core only (the Worker is a generic host), it is always on, and it bundles SQL instrumentation. The exporter keeps both hosts identical and export optional |
| Application Insights SDK (classic) | In maintenance mode, and not OpenTelemetry |
| Logs only, until hosting | Leaves stuck bookings and supplier latency invisible, the key operational risk |
