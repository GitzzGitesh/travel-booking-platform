using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Infrastructure;

namespace TravelBooking.Modules.Payments;

/// <summary>
/// The Payments module's entry point (ADR 0004: it owns the <see cref="Ports.IPaymentProvider"/> port). The host composes
/// a provider adapter. Other modules use <see cref="IOrderPayments"/> only. No endpoints: card entry and webhooks come
/// with the real provider, which waits for ADR 0006 (Q2, Q5).
/// </summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PaymentOperations>();
        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(configuration.GetSection(PaymentReconciliationOptions.SectionName))
            .Validate(options => options.NotFoundConclusiveAfter >= TimeSpan.Zero, "Payments:Reconciliation:NotFoundConclusiveAfter must not be negative.")
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
        return services;
    }
}
