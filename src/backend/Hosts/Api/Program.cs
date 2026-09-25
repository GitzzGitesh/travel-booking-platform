using System.Text.Json.Serialization;
using TravelBooking.Modules.Sample;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
// Numbers must be JSON numbers: the web default also accepts numeric strings, which loosens input validation
// and the API contract. Money amounts are explicit strings by design (api-design rules).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new() { Title = "Travel Booking API", Version = "v1" };
    // Environment-neutral contract: clients configure the base URL themselves.
    document.Servers?.Clear();
    return Task.CompletedTask;
}));
builder.Services.AddSampleModule();
// No fallback policy yet: without an authentication scheme it would turn every unmatched route into a 500.
// It is added with the first authentication scheme (ADR 0008, Phase 4). Until then, deny-by-default is
// enforced by EndpointAuthorizationTests in every environment.
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();

var app = builder.Build();

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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    // The OpenAPI document is served in Development only (allow-list, not "anything but Production").
    // Clients are generated from the committed snapshot, openapi.v1.json, not from a deployed environment.
    app.MapOpenApi().AllowAnonymous();
}

var v1 = app.MapGroup("/api/v1");

if (app.Environment.IsDevelopment())
{
    // Modules.Sample is a Phase 1 spike and is never exposed outside Development.
    v1.MapSampleEndpoints();
}

app.Run();
