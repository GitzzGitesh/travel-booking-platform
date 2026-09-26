using System.Text.Json.Serialization;
using TravelBooking.Api;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Flights.Sabre;
using TravelBooking.Integrations.Flights.Travelport;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Flights;
using TravelBooking.Modules.Orders;
using TravelBooking.Modules.Payments;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the server implementation.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddProblemDetails();
// Numbers must be JSON numbers: the web default also accepts numeric strings, which loosens input validation
// and the API contract. Money amounts are explicit strings by design (api-design rules). Enums are strings.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    // Names only: integers such as "cabin": 2 or 99 are rejected, so the server accepts exactly the documented contract.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
});
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new() { Title = "Travel Booking API", Version = "v1" };
    // Environment-neutral contract: clients configure the base URL themselves.
    document.Servers?.Clear();
    return Task.CompletedTask;
}));
builder.Services.AddFlightsModule(builder.Configuration);
builder.Services.AddOrdersModule(builder.Configuration);
builder.Services.AddPaymentsModule(builder.Configuration);

if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
{
    // The deterministic mock is the only flight provider until a real supplier is chosen (Q6). Allow-listed
    // environments only, matching the mock's own guard, so a production-like environment never gets fake offers.
    builder.Services.AddMockFlightProvider(builder.Configuration);

    // Likewise the only payment provider until ADR 0006 is decided (Q2, Q5): no card data, no network.
    builder.Services.AddMockPaymentProvider(builder.Configuration);
}

// Candidate flight suppliers (Q6): each is composed only when enabled in configuration (Integrations:Flights:<Name>),
// with its credentials from user-secrets / Key Vault. Startup refuses any adapter below ProductionReady outside
// Development and Staging, and a search provider that does not implement search (Flights:SearchProviderId).
builder.Services.AddAmadeusFlightProvider(builder.Configuration);
builder.Services.AddDuffelFlightProvider(builder.Configuration);
builder.Services.AddSabreFlightProvider(builder.Configuration);
builder.Services.AddTravelportFlightProvider(builder.Configuration);

// No fallback policy yet: without an authentication scheme it would turn every unmatched route into a 500.
// It is added with the first authentication scheme (ADR 0008, Phase 4). Until then, deny-by-default is
// enforced by EndpointAuthorizationTests in every environment.
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();
builder.Services.AddApiCors(builder.Configuration);
builder.Services.AddApiForwardedHeaders(builder.Configuration);
builder.Services.AddApiRateLimiting(builder.Configuration);

var app = builder.Build();

// First, so the client address and scheme are right for HSTS, HTTPS redirection and rate limiting. It trusts no proxy
// until a deployment names its ingress (ForwardedHeaders:KnownProxies/KnownNetworks).
app.UseForwardedHeaders();
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    // Before the exception handler, which clears headers on failed responses.
    app.UseHsts();
}

// Malformed requests (invalid JSON, wrong types, oversized bodies) are client errors in every environment,
// not 500s. Everything else stays a generic 500 ProblemDetails without exception details.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
    // Client errors are not server faults: keep them out of error-level exception diagnostics.
    SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException,
});
app.UseStatusCodePages();

app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    // The OpenAPI document is served in Development only (allow-list, not "anything but Production").
    // Clients are generated from the committed snapshot, openapi.v1.json, not from a deployed environment.
    app.MapOpenApi().AllowAnonymous();
}

// Every anonymous API endpoint is rate limited per client (security rules); modules tighten it where they call suppliers.
var v1 = app.MapGroup("/api/v1").RequireRateLimiting(RateLimitPolicies.Anonymous);

if (app.Environment.IsDevelopment())
{
    // Flight endpoints stay Development-only. Rate limiting and the forwarded-headers mechanism exist; widening this gate
    // still needs the hosting decision (trusted ingress addresses, AllowedHosts, a distributed limiter for more than
    // one instance, ADR 0011) and Q2 (docs/progress.md). It must only widen together with IFlightProvider registration.
    v1.MapFlightsEndpoints();
}

app.Run();
