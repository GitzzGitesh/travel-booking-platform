using TravelBooking.Modules.Sample;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSampleModule();
// No fallback policy yet: without an authentication scheme it would turn every unmatched route into a 500.
// It is added with the first authentication scheme (ADR 0008, Phase 4). Until then, deny-by-default is
// enforced by EndpointAuthorizationTests in every environment.
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();

var v1 = app.MapGroup("/api/v1");

if (app.Environment.IsDevelopment())
{
    // Modules.Sample is a Phase 1 spike and is never exposed outside Development.
    v1.MapSampleEndpoints();
}

app.Run();
