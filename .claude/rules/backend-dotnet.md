---
paths:
  - "src/backend/**"
  - "tests/backend/**"
  - "**/*.cs"
  - "**/*.csproj"
---
# Backend (.NET) rules

- Target **.NET 10** (SDK pinned in `global.json`). `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, central package management (`Directory.Packages.props`).
- Async all the way. Pass `CancellationToken` through every async call chain. No `.Result` / `.Wait()`.
- Use the injected `TimeProvider` for time, never `DateTime.Now`/`UtcNow` directly, so offer expiry and deadlines are testable.
- Expected failures (validation, price changed, offer expired, not found, conflict) are returned as **Result types**. Exceptions are for truly exceptional conditions.
- Application handlers are plain classes (a small `ICommandHandler<T>` style defined in BuildingBlocks). **Do not use MediatR, AutoMapper, FluentAssertions v8+, or MassTransit v9+** (commercial licences, see ADR 0003). Map by hand.
- Supplier HTTP calls use typed `HttpClient`s with explicit per-operation timeouts. Resilience (retry / circuit breaker via `Microsoft.Extensions.Http.Resilience`) applies **only to idempotent reads**. Booking/ticketing/payment writes get timeout + circuit breaker, **no retry**.
- Logging uses structured `ILogger` with message templates and source-generated `LoggerMessage` for hot paths. Include correlation, order, and provider reference IDs. Never PII.
- Request validation uses .NET 10 built-in validation (ADR 0003). Each module calls `services.AddValidation()` from its own `Add{Module}Module` method, because the source generator only covers endpoints in the compilation that makes that call. Validated HTTP `*Request` types, including nested request types, must be **public**, named `*Request`, and live in the module's `Endpoints` namespace, because the generator skips internal types. Handlers and everything else stay `internal`. The architecture tests enforce this.
- Configuration uses strongly typed options validated at startup (`ValidateOnStart`). Secrets never have defaults in appsettings.
- EF Core: one `DbContext` per module with its own schema. No lazy loading. Use `AsNoTracking` for reads. Do not expose `IQueryable` outside Infrastructure.
- OpenAPI uses the built-in `Microsoft.AspNetCore.OpenApi`, with Scalar UI in non-production only.
- Every new endpoint, handler, and state transition comes with tests (see `testing.md`).
