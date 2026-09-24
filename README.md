# Travel Booking Platform

A global travel booking platform for flights and hotels, with a customer site and an Admin/Operations portal.

> **Status: engineering foundation.** This repository currently contains only documentation, architecture decisions, and the Claude Code engineering environment. No application code has been written yet. See [`docs/progress.md`](docs/progress.md).

## Planned technology (proposed, see [`docs/adr/`](docs/adr/))

| Area | Choice |
|---|---|
| Backend | ASP.NET Core (.NET 10 LTS), modular monolith, `Api` + `Worker` hosts |
| Frontend | Angular workspace: `customer-web` (SSR) + `admin-web` (SPA) |
| Data | SQL Server / Azure SQL, EF Core, SQL transactional outbox |
| Cache | .NET HybridCache (Redis as L2 when scaled out) |
| Payments | Stripe (manual capture) behind `IPaymentProvider`, plus a mock provider |
| Suppliers | `IFlightProvider` / `IHotelProvider` with mock providers first |
| Identity | Microsoft Entra External ID (customers), Entra ID (staff) |
| Cloud / CI | Azure, GitHub Actions |
| Tests | xUnit v3, Testcontainers, Playwright, OpenAPI diff, provider contract suites |

## Documentation

- [Documentation index](docs/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [Architecture decision records](docs/adr/)
- [Testing strategy](docs/quality/testing-strategy.md)
- [Open questions](docs/requirements/open-questions.md)

## Prerequisites (for upcoming phases)

- .NET SDK 10.0.x
- Node.js 24 LTS
- Docker Desktop (or Podman), required for local SQL Server and integration tests
- GitHub CLI (`gh`)

## Working with Claude Code

Project instructions live in [`CLAUDE.md`](CLAUDE.md), with focused rules, skills, and reviewer agents under `.claude/`.
