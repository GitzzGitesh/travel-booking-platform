using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TravelBooking.Worker;

namespace TravelBooking.ArchitectureTests;

/// <summary>
/// The Worker host (ADR 0007) is a generic host with no HTTP endpoints. It must build in Development, where every
/// registration is validated at build (as at its startup), and it composes no web-host security: authentication schemes
/// and authorization policies belong to the Api, and ASP.NET's authorization services cannot be built without routing
/// (the cause of the Worker's startup crash).
/// </summary>
public sealed class WorkerCompositionTests
{
    [Fact]
    public void The_worker_host_builds_with_every_registration_validated_and_without_web_security()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Never opened: building the host only validates the registrations (no database is needed).
            ["ConnectionStrings:Flights"] = "Server=unused;Database=unused",
            ["ConnectionStrings:Hotels"] = "Server=unused;Database=unused",
            ["ConnectionStrings:Orders"] = "Server=unused;Database=unused",
            ["ConnectionStrings:Payments"] = "Server=unused;Database=unused",
            ["ConnectionStrings:Customers"] = "Server=unused;Database=unused",
            ["ConnectionStrings:Notifications"] = "Server=unused;Database=unused",
        });
        builder.AddWorkerServices();

        using var host = builder.Build(); // Development: ValidateOnBuild and ValidateScopes

        host.Services.GetService<IAuthenticationSchemeProvider>().ShouldBeNull();
        host.Services.GetService<IAuthorizationPolicyProvider>().ShouldBeNull();
    }
}
