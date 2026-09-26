using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Modules.Orders;

/// <summary>
/// The Orders module's entry point (ADR 0005). It maps no endpoints yet: creating orders over HTTP waits for the
/// customer-identity decision (guest checkout, Q8) and payments (ADR 0006).
/// </summary>
public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CreateFlightOrderHandler>();

        // The module's own schema. The connection string is resolved on first use; migrations are never applied at
        // startup (database rules).
        services.AddDbContext<OrdersDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(OrdersDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{OrdersDbContext.ConnectionStringName}' is not configured."),
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IOrderStore, SqlOrderStore>();
        return services;
    }
}
