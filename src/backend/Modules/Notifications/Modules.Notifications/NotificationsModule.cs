using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Modules.Notifications.Application;
using TravelBooking.Modules.Notifications.Infrastructure;
using TravelBooking.Modules.Notifications.Ports;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Modules.Notifications;

/// <summary>
/// The Notifications module (ADR 0024): customer notices recorded from other modules' integration events and sent by the
/// Worker through the <see cref="IEmailSender"/> port. Only the Worker hosts it; it has no endpoints.
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<NotificationsDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(NotificationsDbContext.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{NotificationsDbContext.ConnectionStringName}' is not configured."),
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<INotificationStore, SqlNotificationStore>();
        return services;
    }

    /// <summary>The Worker's part: recording notices from events, and sending them.</summary>
    public static IServiceCollection AddNotificationsBackgroundJobs(this IServiceCollection services)
    {
        services.AddIntegrationEventHandler<OrderBookingSettled, OrderBookingSettledHandler>();
        services.AddIntegrationEventHandler<Payments.Contracts.PaymentRefundSettled, PaymentRefundSettledHandler>();
        services.AddIntegrationEventHandler<OrderCancellationRecorded, OrderCancellationRecordedHandler>();
        services.AddBackgroundJob<SendNotificationsJob, NotificationsDbContext>(SendNotificationsJob.Name, TimeSpan.FromSeconds(30));
        return services;
    }

    /// <summary>The in-memory stand-in provider (Development and Staging only; it refuses any other environment).</summary>
    public static IServiceCollection AddRecordingEmailSender(this IServiceCollection services)
    {
        services.TryAddSingleton<RecordingEmailSender>();
        services.TryAddSingleton<IEmailSender>(provider => provider.GetRequiredService<RecordingEmailSender>());
        return services;
    }
}
