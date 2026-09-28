using System.Text.Json.Serialization;
using TravelBooking.Api;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Flights.Sabre;
using TravelBooking.Integrations.Flights.Travelport;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Integrations.Payments.Stripe;
using TravelBooking.Modules.Customers;
using TravelBooking.Modules.Flights;
using TravelBooking.Modules.Orders;
using TravelBooking.Modules.Payments;

// With a single authentication scheme, ASP.NET makes it the default and authenticates every request. Customer tokens are
// validated only where a customer policy asks for them (ADR 0008).
AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);

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

// Customer identity (ADR 0008): customer bearer tokens (Entra External ID) mapped to our internal customer id. Tenant
// values come from configuration (Authentication:Customers); until they are set, every customer token is refused.
builder.Services.AddCustomersModule(builder.Configuration);

if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
{
    // The deterministic mock is the only flight provider until a real supplier is chosen (Q6). Allow-listed
    // environments only, matching the mock's own guard, so a production-like environment never gets fake offers.
    builder.Services.AddMockFlightProvider(builder.Configuration);

    // Likewise the payment provider unless Stripe is enabled (ADR 0006): no card data, no network.
    if (!builder.Configuration.IsStripeEnabled())
    {
        builder.Services.AddMockPaymentProvider(builder.Configuration);
    }
}

// Stripe (ADR 0006), composed only when enabled (Integrations:Payments:Stripe), with test-mode keys from user-secrets /
// Key Vault. Not production-ready: startup refuses it outside Development and Staging.
builder.Services.AddStripePaymentProvider(builder.Configuration);

// Candidate flight suppliers (Q6): each is composed only when enabled in configuration (Integrations:Flights:<Name>),
// with its credentials from user-secrets / Key Vault. Startup refuses any adapter below ProductionReady outside
// Development and Staging, and a search provider that does not implement search (Flights:SearchProviderId).
builder.Services.AddAmadeusFlightProvider(builder.Configuration);
builder.Services.AddDuffelFlightProvider(builder.Configuration);
builder.Services.AddSabreFlightProvider(builder.Configuration);
builder.Services.AddTravelportFlightProvider(builder.Configuration);

// Deny by default is enforced by EndpointAuthorizationTests, in every environment: each endpoint declares a policy or
// an explicit AllowAnonymous. No fallback policy: ASP.NET applies it to requests that match no endpoint too, which
// would turn every unknown route's 404 into a 401.
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
app.UseAuthentication();
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

// The payment provider's notifications (webhooks): mapped only when the composed provider sends them.
v1.MapPaymentsEndpoints();

if (app.Environment.IsDevelopment())
{
    // Flight endpoints stay Development-only. Rate limiting and the forwarded-headers mechanism exist; widening this gate
    // still needs the hosting decision (trusted ingress addresses, AllowedHosts, a distributed limiter for more than
    // one instance, ADR 0011) and Q2 (docs/progress.md). It must only widen together with IFlightProvider registration.
    v1.MapFlightsEndpoints();

    // The signed-in customer's endpoints: behind the same gate as the flights they order.
    v1.MapCustomersEndpoints();
    v1.MapOrdersEndpoints();
}

app.Run();
