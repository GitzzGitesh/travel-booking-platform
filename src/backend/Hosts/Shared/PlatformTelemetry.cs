using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TravelBooking.Hosts.Observability;

/// <summary>
/// OpenTelemetry for both hosts (ADR 0031): traces, metrics and logs with one resource (service name, version,
/// environment). Our own sources and meters are named <c>TravelBooking.*</c>. Exported to Azure Monitor when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set (Key Vault or the host's environment, never appsettings), and to
/// OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (a local Aspire dashboard, or a collector); otherwise nothing is
/// exported. No personal data: URL query values are redacted by the instrumentation, SQL statements are not captured,
/// and logs carry ids only (security rules).
/// </summary>
public static class PlatformTelemetry
{
    /// <summary>The prefix of every activity source and meter the platform defines.</summary>
    public const string SourcePrefix = "TravelBooking.";

    public const string AzureMonitorSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    public const string OtlpEndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>EF Core's log categories: below Warning, never exported.</summary>
    public const string EntityFrameworkCategory = "Microsoft.EntityFrameworkCore";

    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var azureMonitor = builder.Configuration[AzureMonitorSetting] is { Length: > 0 } connectionString ? connectionString : null;
        var otlp = builder.Configuration[OtlpEndpointSetting] is { Length: > 0 };
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(SourcePrefix + "*")
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(SourcePrefix + "*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
        });

        // EF Core logs every command's SQL at Information: never exported (SQL text is not telemetry, and a later
        // sensitive-data logging switch would ship parameter values). Warnings and errors still are.
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(EntityFrameworkCategory, LogLevel.Warning);

        if (otlp)
        {
            telemetry.UseOtlpExporter();
        }

        if (azureMonitor is not null)
        {
            telemetry.WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = azureMonitor))
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = azureMonitor));
            builder.Logging.AddOpenTelemetry(logging => logging.AddAzureMonitorLogExporter(options => options.ConnectionString = azureMonitor));
        }

        builder.Services.AddSingleton(new TelemetryExport(azureMonitor is not null, otlp));
        return builder;
    }
}

/// <summary>Where telemetry goes in this host (startup reports it; Production should export somewhere).</summary>
public sealed record TelemetryExport(bool AzureMonitor, bool Otlp)
{
    public bool Any => AzureMonitor || Otlp;
}
