using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TravelBooking.Integrations.Payments.Mock.Persistence;
using TravelBooking.Modules.Payments.Ports;
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
        using var host = Worker(Environments.Development); // Development: ValidateOnBuild and ValidateScopes

        host.Services.GetService<IAuthenticationSchemeProvider>().ShouldBeNull();
        host.Services.GetService<IAuthorizationPolicyProvider>().ShouldBeNull();
    }

    // ADR 0032: in Production the Worker composes neither the mock payment provider nor its state, and never falls back to
    // it: with no production-ready provider configured, it has no payment provider at all (payment work fails at first use).
    [Fact]
    public void The_production_worker_composes_neither_the_mock_payment_provider_nor_its_state()
    {
        using var host = Worker(Environments.Production);

        host.Services.GetServices<IPaymentProvider>().ShouldBeEmpty();
        host.Services.GetService<DbContextOptions<MockPaymentsDbContext>>().ShouldBeNull();
        host.Services.GetService<MockPaymentsDbContext>().ShouldBeNull();
    }

    private static IHost Worker(string environment)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, Args = [] });
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
        return builder.Build();
    }
}
