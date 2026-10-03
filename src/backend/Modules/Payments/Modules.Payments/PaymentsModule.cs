using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.BuildingBlocks.Http;
using TravelBooking.Modules.Access.Contracts;
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
    /// <summary>The Payments outbox (refund outcomes, ADR 0027), dispatched by the Worker.</summary>
    internal const string OutboxJobName = "payments.outbox";

    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PaymentOperations>();
        services.AddOptions<PaymentAttemptLimits>()
            .Bind(configuration.GetSection(PaymentAttemptLimits.SectionName))
            .Validate(options => options.MaxAttemptsPerOrder > 0 && options.MaxAttemptsPerCustomerPerDay > 0 && options.AlertAfterTrips > 0, "Payments:AttemptLimits values must be positive.")
            .ValidateOnStart();
        services.AddOptions<PaymentHoldOptions>()
            .Bind(configuration.GetSection(PaymentHoldOptions.SectionName))
            .Validate(options => options.IsValid(), "Payments:Holds needs 0 < WarningBefore < Lifetime.")
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
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema);

                // Transient database errors (a deadlock victim under parallel same-key requests, a dropped connection) are
                // retried, as in Orders and Flights. Only database statements are retried, never a payment provider call;
                // a re-applied insert meets the attempt, inbox and trip unique constraints ("the same request won") and a
                // re-applied update fails its rowversion check and is read again.
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IPaymentAttemptStore, SqlPaymentAttemptStore>();
        services.AddScoped<AuthorizeOrderPaymentHandler>();
        services.AddScoped<IOrderPayments>(provider => provider.GetRequiredService<AuthorizeOrderPaymentHandler>());
        services.AddScoped<IPaymentNotificationStore, SqlPaymentNotificationStore>();

        // The way out of ManualReview (an operations action; its admin endpoint comes with staff identity).
        services.AddScoped<ResolvePaymentReviewHandler>();
        services.AddScoped<ResolveRefundReviewHandler>();
        services.AddScoped<PaymentAttemptReviewList>();
        services.AddValidation(); // the request types in this module's Endpoints namespace (ADR 0003)
        services.AddScoped<ReceivePaymentNotificationHandler>();

        // Checked at startup in both hosts: one provider, and only a production-ready one outside Development and Staging.
        services.AddSingleton<IValidateOptions<PaymentProviderComposition>, PaymentProviderCompositionValidator>();
        services.AddOptions<PaymentProviderComposition>().ValidateOnStart();
        return services;
    }

    /// <summary>
    /// The staff endpoints for payments (ADR 0022), under the admin route group: staff with MFA and the named permission
    /// only. Resolving a review is audited in the same save, and calls the provider (supplier-call rate limit).
    /// </summary>
    public static IEndpointRouteBuilder MapPaymentsAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/payments").WithTags("Payments (staff)");

        group.MapGet("/attempt-limit-reviews", AdminPaymentEndpoints.AttemptLimitReview)
            .WithName("ListAttemptLimitReviews")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.PaymentsRead))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{attemptId:guid}", AdminPaymentEndpoints.Get)
            .WithName("GetPaymentAttemptForOperations")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.PaymentsRead))
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{attemptId:guid}/review-resolutions", AdminPaymentEndpoints.Resolve)
            .WithName("ResolvePaymentReview")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.PaymentsReviewResolve))
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls) // every resolution is a provider lookup
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refunds/{refundId:guid}/review-resolutions", AdminPaymentEndpoints.ResolveRefund)
            .WithName("ResolveRefundReview")
            .RequireAuthorization(StaffIdentity.PolicyFor(StaffPermissions.PaymentsReviewResolve))
            .RequireRateLimiting(RateLimitPolicies.SupplierCalls) // every resolution is a provider lookup
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return endpoints;
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
        services.AddIntegrationEventHandler<OrderPaymentCaptureRequested, OrderPaymentCaptureRequestedHandler>();
        services.AddBackgroundJob<ProcessPaymentNotificationsJob, PaymentsDbContext>(ProcessPaymentNotificationsJob.Name, TimeSpan.FromSeconds(10));
        services.AddBackgroundJob<WatchExpiringHoldsJob, PaymentsDbContext>(WatchExpiringHoldsJob.Name, TimeSpan.FromMinutes(15));

        // Refunds (ADR 0027): recorded from Orders' approval, sent once under our key, looked up when unknown; their outcome
        // goes to Orders and the customer's notice through the Payments outbox.
        services.AddIntegrationEventHandler<OrderRefundRequested, OrderRefundRequestedHandler>();
        services.AddBackgroundJob<ExecuteRefundsJob, PaymentsDbContext>(ExecuteRefundsJob.Name, TimeSpan.FromSeconds(30));
        services.AddOutboxDispatcher<PaymentsDbContext>(OutboxJobName, TimeSpan.FromSeconds(5));
        return services;
    }
}
