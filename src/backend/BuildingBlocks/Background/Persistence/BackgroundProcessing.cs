using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TravelBooking.BuildingBlocks.Background.Persistence;

/// <summary>The job lease in the module's own schema. Acquiring is one conditional UPDATE, so two Workers cannot both win.</summary>
public sealed class EfJobLeaseStore<TContext>(TContext db, TimeProvider timeProvider) : IJobLeaseStore
    where TContext : DbContext
{
    // SQL Server duplicate-key errors: unique index (2601) and primary key or unique constraint (2627).
    private static readonly int[] _uniqueViolations = [2601, 2627];

    public async Task<bool> TryAcquireAsync(string jobName, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var until = now + duration;
        var taken = await db.Set<JobLease>()
            .Where(l => l.Name == jobName && (l.LeaseUntil <= now || l.Owner == owner))
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.Owner, owner).SetProperty(l => l.LeaseUntil, until), cancellationToken);
        if (taken == 1)
        {
            return true;
        }

        if (await db.Set<JobLease>().AnyAsync(l => l.Name == jobName, cancellationToken))
        {
            return false; // held by another owner
        }

        // First run of this job anywhere: whoever inserts the row holds the lease.
        var lease = db.Set<JobLease>().Add(JobLease.Take(jobName, owner, until));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: var number } && _uniqueViolations.Contains(number))
        {
            lease.State = EntityState.Detached;
            return false;
        }
    }

    public Task ReleaseAsync(string jobName, string owner, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.Set<JobLease>()
            .Where(l => l.Name == jobName && l.Owner == owner)
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.LeaseUntil, now), cancellationToken);
    }
}

/// <summary>How to deliver one integration event type to its handlers (built at registration).</summary>
public sealed record IntegrationEventRegistration(string Type, Func<IServiceProvider, string, CancellationToken, Task> Dispatch)
{
    public static IntegrationEventRegistration For<TEvent>()
        where TEvent : IIntegrationEvent =>
        new(typeof(TEvent).FullName!, async (services, payload, cancellationToken) =>
        {
            var integrationEvent = JsonSerializer.Deserialize<TEvent>(payload, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Empty payload for {typeof(TEvent).Name}.");
            var handlers = services.GetServices<IIntegrationEventHandler<TEvent>>().ToList();
            if (handlers.Count == 0)
            {
                throw new InvalidOperationException($"No handler is registered for {typeof(TEvent).Name}.");
            }

            foreach (var handler in handlers)
            {
                await handler.HandleAsync(integrationEvent, cancellationToken);
            }
        });
}

/// <summary>
/// Dispatches a module's pending outbox messages to their handlers, oldest first (ADR 0007). Each message is handled in
/// its own DI scope, so one failure never leaks tracked changes into the next. Delivery is at least once: a crash after a
/// handler succeeded but before the message is marked processed redelivers it, and the handler's inbox makes that a no-op.
/// A failing message backs off exponentially and is given up on (<see cref="OutboxMessage.FailedAt"/>) after
/// <see cref="MaxAttempts"/> attempts, with an error log for alerting.
/// </summary>
public sealed partial class OutboxDispatcher<TContext>(
    TContext db,
    IEnumerable<IntegrationEventRegistration> registrations,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher<TContext>> logger) : IBackgroundJob
    where TContext : DbContext
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 10;

    // Traces continue across the async hop (ADR 0007): each dispatch is a child of the request that raised the event.
    private static readonly ActivitySource _activitySource = new("TravelBooking.Outbox");

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var handled = 0;
        var batch = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null && m.FailedAt == null && m.NextAttemptAt <= now)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            if (BackgroundJobRun.IsOver(now, timeProvider))
            {
                break;
            }

            handled++;
            using var activity = _activitySource.StartActivity($"outbox {message.Type}", ActivityKind.Consumer, message.TraceParent);
            activity?.SetTag("correlation.id", message.CorrelationId);
            try
            {
                var registration = registrations.FirstOrDefault(r => r.Type == message.Type)
                    ?? throw new InvalidOperationException($"Unknown integration event type {message.Type}.");
                await using var scope = scopes.CreateAsyncScope();
                await registration.Dispatch(scope.ServiceProvider, message.Payload, cancellationToken);
                message.MarkProcessed(timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.MarkAttemptFailed(timeProvider.GetUtcNow(), exception.GetType().Name, MaxAttempts);
                if (message.FailedAt is not null)
                {
                    LogGivenUp(logger, message.Id, message.Type, message.Attempts, exception.GetType().Name);
                }
                else
                {
                    LogAttemptFailed(logger, message.Id, message.Type, message.Attempts, exception.GetType().Name);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) failed attempt {Attempts}: {Error}")]
    private static partial void LogAttemptFailed(ILogger logger, Guid messageId, string type, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) was given up on after {Attempts} attempts: {Error}")]
    private static partial void LogGivenUp(ILogger logger, Guid messageId, string type, int attempts, string error);
}

/// <summary>A registered job: what to run, how often, and which module's lease guards it.</summary>
public sealed record BackgroundJobSchedule(string Name, Type JobType, Type LeaseStoreType, TimeSpan Interval, TimeSpan LeaseDuration);

/// <summary>
/// Runs every registered job on its interval, each run under its lease (ADR 0007: a hosted-service scheduler with a DB
/// lease). A Worker that dies holding a lease blocks that job only until the lease expires; another Worker then takes it.
/// </summary>
public sealed partial class BackgroundJobRunner(
    IEnumerable<BackgroundJobSchedule> schedules,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<BackgroundJobRunner> logger) : BackgroundService
{
    /// <summary>This process's identity on the leases.</summary>
    public string Owner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Runs the job once if its lease can be taken; false when another Worker holds it.</summary>
    public async Task<bool> RunOnceAsync(BackgroundJobSchedule schedule, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var leases = (IJobLeaseStore)scope.ServiceProvider.GetRequiredService(schedule.LeaseStoreType);
        if (!await leases.TryAcquireAsync(schedule.Name, Owner, schedule.LeaseDuration, cancellationToken))
        {
            return false;
        }

        try
        {
            var job = (IBackgroundJob)scope.ServiceProvider.GetRequiredService(schedule.JobType);
            var handled = await job.RunOnceAsync(cancellationToken);
            if (handled > 0)
            {
                LogRun(logger, schedule.Name, handled);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, schedule.Name, exception);
        }
        finally
        {
            await leases.ReleaseAsync(schedule.Name, Owner, CancellationToken.None);
        }

        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(schedules.Select(schedule => RunScheduleAsync(schedule, stoppingToken)));

    private async Task RunScheduleAsync(BackgroundJobSchedule schedule, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(schedule, stoppingToken);
                await Task.Delay(schedule.Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The lease store itself failed (e.g. the database is down): wait and try again.
                LogFailed(logger, schedule.Name, exception);
                try
                {
                    await Task.Delay(schedule.Interval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Background job {Job} handled {Count} items")]
    private static partial void LogRun(ILogger logger, string job, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Background job {Job} failed")]
    private static partial void LogFailed(ILogger logger, string job, Exception exception);
}

public static class BackgroundServiceCollectionExtensions
{
    /// <summary>Registers a leased job, guarded by a lease in <typeparamref name="TLeaseContext"/>'s schema.</summary>
    public static IServiceCollection AddBackgroundJob<TJob, TLeaseContext>(this IServiceCollection services, string name, TimeSpan interval)
        where TJob : class, IBackgroundJob
        where TLeaseContext : DbContext
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, JobLease.MaxNameLength, nameof(name));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<EfJobLeaseStore<TLeaseContext>>();
        services.AddScoped<TJob>();
        services.AddSingleton(new BackgroundJobSchedule(name, typeof(TJob), typeof(EfJobLeaseStore<TLeaseContext>), interval, BackgroundJobRun.LeaseDuration));
        return services;
    }

    /// <summary>Dispatches <typeparamref name="TContext"/>'s outbox on a leased schedule.</summary>
    public static IServiceCollection AddOutboxDispatcher<TContext>(this IServiceCollection services, string name, TimeSpan interval)
        where TContext : DbContext =>
        services.AddBackgroundJob<OutboxDispatcher<TContext>, TContext>(name, interval);

    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        if (!services.Any(d => d.ServiceType == typeof(IntegrationEventRegistration) && d.ImplementationInstance is IntegrationEventRegistration r && r.Type == typeof(TEvent).FullName))
        {
            services.AddSingleton(IntegrationEventRegistration.For<TEvent>());
        }

        return services;
    }

    /// <summary>Hosts the scheduler: the Worker only (ADR 0007: background work never runs in the Api).</summary>
    public static IServiceCollection AddBackgroundJobRunner(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BackgroundJobRunner>();
        services.AddHostedService(provider => provider.GetRequiredService<BackgroundJobRunner>());
        return services;
    }
}
