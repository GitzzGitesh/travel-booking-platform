using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Endpoints;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Modules.Orders;

/// <summary>
/// The Orders module's entry point (ADR 0005). Its endpoints are the signed-in customer's own orders (ADR 0008; Q8:
/// sign-in required). Checkout (payment) has no endpoint yet: see the preconditions in docs/progress.md.
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
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)
        return services;
    }

    /// <summary>The customer's order endpoints: every one requires a signed-in customer and acts on their own orders only.</summary>
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/orders").WithTags("Orders").RequireAuthorization(CustomerIdentity.Policy);

        group.MapPost("/", OrderEndpoints.Create)
            .WithName("CreateFlightOrder")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{orderId:guid}", OrderEndpoints.Get)
            .WithName("GetOrder")
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        return endpoints;
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
