using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Endpoints;
using TravelBooking.Modules.Payments.Infrastructure;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments;

/// <summary>
/// The Payments module's entry point (ADR 0004: it owns the <see cref="Ports.IPaymentProvider"/> port). The host composes
/// one provider adapter. Other modules use <see cref="IOrderPayments"/> only. Its one endpoint receives the provider's
/// notifications (webhooks), mapped only when the composed provider sends them (ADR 0006).
/// </summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PaymentOperations>();
        services.AddOptions<PaymentAttemptLimits>()
            .Bind(configuration.GetSection(PaymentAttemptLimits.SectionName))
            .Validate(options => options.MaxAttemptsPerOrder > 0 && options.MaxAttemptsPerCustomerPerDay > 0, "Payments:AttemptLimits values must be positive.")
            .ValidateOnStart();
        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(configuration.GetSection(PaymentReconciliationOptions.SectionName))
            .Validate(options => options.NotFoundConclusiveAfter >= TimeSpan.Zero, "Payments:Reconciliation:NotFoundConclusiveAfter must not be negative.")
            .Validate(options => options.LookupAfter >= TimeSpan.Zero, "Payments:Reconciliation:LookupAfter must not be negative.")
            .ValidateOnStart();

        // The module's own schema. The connection string is resolved on first use; migrations are never applied at
        // startup (database rules).
        services.AddDbContext<PaymentsDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(PaymentsDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{PaymentsDbContext.ConnectionStringName}' is not configured."),
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema)));
        services.AddScoped<IPaymentAttemptStore, SqlPaymentAttemptStore>();
        services.AddScoped<AuthorizeOrderPaymentHandler>();
        services.AddScoped<IOrderPayments>(provider => provider.GetRequiredService<AuthorizeOrderPaymentHandler>());
        services.AddScoped<IPaymentNotificationStore, SqlPaymentNotificationStore>();

        // The way out of ManualReview (an operations action; its admin endpoint comes with staff identity).
        services.AddScoped<ResolvePaymentReviewHandler>();
        services.AddScoped<ReceivePaymentNotificationHandler>();

        // Checked at startup in both hosts: one provider, and only a production-ready one outside Development and Staging.
        services.AddSingleton<IValidateOptions<PaymentProviderComposition>, PaymentProviderCompositionValidator>();
        services.AddOptions<PaymentProviderComposition>().ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Maps the provider notification (webhook) endpoint, only when the composed provider sends notifications. Anonymous
    /// by design: the provider's signature authenticates it. Not part of the client API (excluded from OpenAPI).
    /// </summary>
    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        if (!endpoints.ServiceProvider.GetServices<IPaymentNotifications>().Any())
        {
            return endpoints;
        }

        endpoints.MapPost("/payments/notifications/{providerId}", PaymentNotificationEndpoint.Handle)
            .WithName("ReceivePaymentNotification")
            .ExcludeFromDescription()
            .AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// The Worker's Payments jobs (ADR 0007): reconciling payment attempts and releasing holds Orders will not use, and
    /// recording Orders' release requests (inbox). Never registered in the Api.
    /// </summary>
    public static IServiceCollection AddPaymentsBackgroundJobs(this IServiceCollection services)
    {
        services.AddScoped<PaymentAttemptReconciler>();
        services.AddBackgroundJob<ReconcilePaymentAttemptsJob, PaymentsDbContext>(ReconcilePaymentAttemptsJob.Name, TimeSpan.FromSeconds(30));
        services.AddIntegrationEventHandler<OrderPaymentReleaseRequested, OrderPaymentReleaseRequestedHandler>();
        services.AddBackgroundJob<ProcessPaymentNotificationsJob, PaymentsDbContext>(ProcessPaymentNotificationsJob.Name, TimeSpan.FromSeconds(10));
        return services;
    }
}
