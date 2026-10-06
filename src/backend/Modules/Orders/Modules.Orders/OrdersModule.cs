using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Contracts;
using TravelBooking.Modules.Orders.Endpoints;
using TravelBooking.Modules.Orders.Infrastructure;

namespace TravelBooking.Modules.Orders;

/// <summary>
/// The Orders module's entry point (ADR 0005). Its endpoints are the signed-in customer's own orders (ADR 0008; Q8:
/// sign-in required), including checkout: authorize → book → capture (ADR 0005, ADR 0021).
/// </summary>
public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OrderItemSelections>();
        services.AddScoped<CreateFlightOrderHandler>();
        services.AddScoped<AuthorizeCheckoutHandler>();
        services.AddScoped<FlightBookingOrchestrator>();
        services.AddScoped<ResolveBookingReviewHandler>();
        services.AddOptions<BookingReconciliationOptions>()
            .Bind(configuration.GetSection(BookingReconciliationOptions.SectionName))
            .Validate(o => o.LookupAfter > TimeSpan.Zero && o.NotFoundConclusiveAfter >= o.LookupAfter && o.ManualReviewAfter > o.NotFoundConclusiveAfter,
                "Orders:BookingReconciliation needs 0 < LookupAfter <= NotFoundConclusiveAfter < ManualReviewAfter.")
            .ValidateOnStart();
        services.AddScoped<IOrderTravellerNeeds, OrderTravellerNeedsQuery>();

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
        services.AddScoped<IRefundCaseStore, SqlRefundCaseStore>();
        services.AddScoped<ICancellationRequestStore, SqlCancellationRequestStore>();
        services.AddScoped<IOrderHistory, SqlOrderHistory>();
        services.AddScoped<CancellationRequestHandler>();
        services.AddScoped<RefundCaseHandler>();
        services.AddOptions<RefundOptions>()
            .Bind(configuration.GetSection(RefundOptions.SectionName))
            .Validate(o => o.IsValid(), "Refunds: fees must not be negative, and ExecutionTargetDays must be positive.")
            .ValidateOnStart();
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)
        return services;
    }

    /// <summary>The customer's order endpoints: every one requires a signed-in customer and acts on their own orders only.</summary>
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/orders").WithTags("Orders").RequireAuthorization(CustomerIdentity.Policy)
            .RequireCustomerWriteLimit(); // per customer (Q10); per address too, from the /api/v1 group

        group.MapPost("/", OrderEndpoints.Create)
            .WithName("CreateFlightOrder")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{orderId:guid}/checkout", OrderEndpoints.Checkout)
            .WithName("CheckoutOrder")
            .ProducesValidationProblem()
            .Produces<CheckoutResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{orderId:guid}", OrderEndpoints.Get)
            .WithName("GetOrder")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // ADR 0029: "My trips", and asking to cancel (operations act on it; nothing is cancelled here).
        group.MapGet("/", CancellationRequestEndpoints.ListMine)
            .WithName("ListMyOrders")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        group.MapPost("/{orderId:guid}/cancellation-requests", CancellationRequestEndpoints.Request)
            .WithName("RequestCancellation")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/{orderId:guid}/cancellation-requests/{requestId:guid}/withdrawal", CancellationRequestEndpoints.Withdraw)
            .WithName("WithdrawCancellationRequest")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return endpoints;
    }

    /// <summary>
    /// The staff endpoints for orders (ADR 0022), under the admin route group: each requires a staff member with MFA and
    /// the named permission, never a customer token. Every change is audited in the same save.
    /// </summary>
    public static IEndpointRouteBuilder MapOrdersAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/orders").WithTags("Orders (staff)");

        group.MapGet("/", AdminOrderEndpoints.Queue)
            .WithName("ListOrdersForOperations")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.OrdersRead))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{orderId:guid}", AdminOrderEndpoints.Get)
            .WithName("GetOrderForOperations")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.OrdersRead))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{orderId:guid}/items/{itemId:guid}/review-checks", AdminOrderEndpoints.CheckReview)
            .WithName("CheckBookingReview")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.BookingsReviewResolve))
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls) // every check is a (paid) supplier lookup
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // ADR 0025: a person's outcome (no supplier call): cancelled at the supplier's desk, or accepted as booked.
        group.MapPost("/{orderId:guid}/items/{itemId:guid}/review-outcomes", AdminOrderEndpoints.RecordOutcome)
            .WithName("RecordBookingReviewOutcome")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.BookingsReviewResolve))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // ADR 0027: cancellations and refunds; a refund is approved by a different person (maker-checker).
        group.MapPost("/{orderId:guid}/refund-cases", AdminRefundEndpoints.Open)
            .WithName("OpenRefundCase")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsRequest))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{orderId:guid}/refund-cases", AdminRefundEndpoints.ForOrder)
            .WithName("ListOrderRefundCases")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.OrdersRead))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // ADR 0029: customers' cancellation requests: completed by opening the cancellation case, or declined.
        var cancellations = endpoints.MapGroup("/cancellation-requests").WithTags("Cancellation requests (staff)");
        cancellations.MapGet("/", CancellationRequestEndpoints.Open)
            .WithName("ListOpenCancellationRequests")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsRequest))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        cancellations.MapPost("/{requestId:guid}/decline", CancellationRequestEndpoints.Decline)
            .WithName("DeclineCancellationRequest")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsRequest))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var refunds = endpoints.MapGroup("/refund-cases").WithTags("Refunds (staff)");
        refunds.MapGet("/", AdminRefundEndpoints.Pending)
            .WithName("ListPendingRefundCases")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsApprove))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        refunds.MapPost("/{caseId:guid}/decision", AdminRefundEndpoints.Decide)
            .WithName("DecideRefundCase")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsApprove))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        refunds.MapPost("/{caseId:guid}/withdrawal", AdminRefundEndpoints.Withdraw)
            .WithName("WithdrawRefundCase")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.RefundsRequest))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    /// <summary>
    /// The Worker's Orders jobs (ADR 0007): dispatching the Orders outbox, expiring orders whose offer lapsed before
    /// payment, and looking up bookings whose outcome is unknown. Never registered in the Api.
    /// </summary>
    public static IServiceCollection AddOrdersBackgroundJobs(this IServiceCollection services)
    {
        services.AddScoped<ExpireUnpaidOrderHandler>();
        services.AddBackgroundJob<ReconcileBookingsJob, OrdersDbContext>(ReconcileBookingsJob.Name, TimeSpan.FromSeconds(30));
        services.AddOutboxDispatcher<OrdersDbContext>(OrdersOutboxJobName, TimeSpan.FromSeconds(5));
        services.AddBackgroundJob<ExpireUnpaidOrdersJob, OrdersDbContext>(ExpireUnpaidOrdersJob.Name, TimeSpan.FromMinutes(1));
        services.AddIntegrationEventHandler<TravelBooking.Modules.Payments.Contracts.PaymentRefundSettled, PaymentRefundSettledHandler>(); // ADR 0027
        services.AddBackgroundJob<WatchRefundCasesJob, OrdersDbContext>(WatchRefundCasesJob.Name, TimeSpan.FromHours(1));
        return services;
    }

    internal const string OrdersOutboxJobName = "orders.outbox";
}
