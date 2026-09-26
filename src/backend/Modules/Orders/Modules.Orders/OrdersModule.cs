using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Modules.Orders;

/// <summary>
/// The Orders module's entry point (ADR 0005). It maps no endpoints yet: creating orders over HTTP waits for the
/// identity provider (ADR 0008; Q8: sign-in required) and the payment provider (ADR 0006).
/// </summary>
public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CreateFlightOrderHandler>();
        services.AddScoped<AuthorizeCheckoutHandler>();

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

    /// <summary>
    /// The Worker's Orders jobs (ADR 0007): dispatching the Orders outbox, and expiring orders whose offer lapsed before
    /// payment. Never registered in the Api.
    /// </summary>
    public static IServiceCollection AddOrdersBackgroundJobs(this IServiceCollection services)
    {
        services.AddScoped<ExpireUnpaidOrderHandler>();
        services.AddOutboxDispatcher<OrdersDbContext>(OrdersOutboxJobName, TimeSpan.FromSeconds(5));
        services.AddBackgroundJob<ExpireUnpaidOrdersJob, OrdersDbContext>(ExpireUnpaidOrdersJob.Name, TimeSpan.FromMinutes(1));
        return services;
    }

    internal const string OrdersOutboxJobName = "orders.outbox";
}
