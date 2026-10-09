using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TravelBooking.Integrations.Payments.Mock.Persistence;

/// <summary>Composes the shared ledger (ADR 0032): the mock's own schema in the Payments database.</summary>
internal static class MockPaymentPersistence
{
    public static IServiceCollection AddSqlMockPaymentLedger(this IServiceCollection services, IConfiguration configuration)
    {
        // The connection string is resolved on first use; migrations are never applied at startup (database rules).
        // No automatic retry: a call is one transaction under an application lock, and a failure is reported to the
        // caller as the provider being unavailable (or an unknown outcome), as a real provider's would be.
        services.AddDbContextFactory<MockPaymentsDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(MockPaymentsDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{MockPaymentsDbContext.ConnectionStringName}' is not configured (the mock payment provider keeps its payments there)."),
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", MockPaymentsDbContext.Schema)));
        services.AddSingleton<IMockPaymentLedger, SqlMockPaymentLedger>();
        return services;
    }
}
