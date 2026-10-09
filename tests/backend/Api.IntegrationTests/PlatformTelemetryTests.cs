using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using TravelBooking.Hosts.Observability;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// OpenTelemetry in the Api (ADR 0031): always instrumented, exported only where configured (Azure Monitor by its
/// connection string, OTLP by its endpoint), the platform's own <c>TravelBooking.*</c> sources traced, and health probes
/// never traced.
/// </summary>
public sealed class PlatformTelemetryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Without_settings_the_api_is_instrumented_but_exports_nothing()
    {
        factory.Services.GetRequiredService<TelemetryExport>().ShouldBe(new TelemetryExport(AzureMonitor: false, Otlp: false));
        factory.Services.GetService<TracerProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void An_otlp_endpoint_turns_the_otlp_export_on()
    {
        using var configured = factory.WithWebHostBuilder(host => host.UseSetting(PlatformTelemetry.OtlpEndpointSetting, "http://localhost:4317"));

        configured.Services.GetRequiredService<TelemetryExport>().ShouldBe(new TelemetryExport(AzureMonitor: false, Otlp: true));
    }

    // Pins the redaction this ADR relies on: a query value never reaches a span (a package bump or a switch could change it).
    [Fact]
    public async Task Query_values_never_reach_an_exported_span()
    {
        var spans = new List<Activity>();
        using var configured = factory.WithWebHostBuilder(host => host.ConfigureServices(services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(spans))));
        using var client = configured.CreateClient();

        (await client.GetAsync("/openapi/v1.json?lastName=Lovelace-secret", TestContext.Current.CancellationToken)).Dispose();

        // The server span ends as the pipeline completes, which can be just after the client has the response.
        var tracer = configured.Services.GetRequiredService<TracerProvider>();
        for (var i = 0; i < 50 && !spans.Any(s => s.Kind == ActivityKind.Server); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            tracer.ForceFlush();
        }

        spans.ShouldContain(s => s.Kind == ActivityKind.Server);
        var values = spans.SelectMany(s => s.TagObjects).Select(tag => Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToList();
        values.ShouldNotContain(value => value.Contains("Lovelace-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Entity_framework_sql_logs_are_never_exported_below_warning()
    {
        var rules = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Logging.LoggerFilterOptions>>().Value.Rules;

        rules.ShouldContain(rule => rule.ProviderName == typeof(OpenTelemetry.Logs.OpenTelemetryLoggerProvider).FullName
            && rule.CategoryName == PlatformTelemetry.EntityFrameworkCategory && rule.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task The_platforms_own_sources_are_traced_and_health_probes_are_not()
    {
        var spans = new List<Activity>();
        using var configured = factory.WithWebHostBuilder(host => host.ConfigureServices(services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(spans))));
        using var client = configured.CreateClient();
        var tracer = configured.Services.GetRequiredService<TracerProvider>();

        using (var source = new ActivitySource("TravelBooking.Test"))
        using (source.StartActivity("platform-span"))
        {
        }

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        tracer.ForceFlush();

        spans.ShouldContain(s => s.OperationName == "platform-span");
        spans.ShouldNotContain(s => s.DisplayName.Contains("health", StringComparison.OrdinalIgnoreCase));
    }
}
