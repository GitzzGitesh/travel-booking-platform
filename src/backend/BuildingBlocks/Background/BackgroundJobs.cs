namespace TravelBooking.BuildingBlocks.Background;

/// <summary>
/// A unit of background work that the Worker runs on a schedule, under a database lease so that only one Worker process
/// runs it at a time (ADR 0007). Each run handles a bounded batch. A job must be idempotent: a run repeated after a crash,
/// or by another Worker once the lease has expired, has no extra effect.
/// </summary>
public interface IBackgroundJob
{
    /// <returns>How many items the run handled.</returns>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);
}

public static class BackgroundJobRun
{
    /// <summary>The lease a Worker takes for one run; a crashed Worker's lease frees itself after this.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A run stops taking new items after this, well inside <see cref="LeaseDuration"/>, so another Worker never takes the
    /// lease over while this run is still working. What is left waits for the next run.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    public static bool IsOver(DateTimeOffset startedAt, TimeProvider timeProvider) => timeProvider.GetUtcNow() - startedAt >= Budget;
}

/// <summary>A lease on a named job, held in the owning module's schema (ADR 0002: no shared tables).</summary>
public interface IJobLeaseStore
{
    /// <summary>Takes the lease if it is free, expired, or already ours; false if another owner holds it.</summary>
    Task<bool> TryAcquireAsync(string jobName, string owner, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Releases the lease if we still hold it.</summary>
    Task ReleaseAsync(string jobName, string owner, CancellationToken cancellationToken);
}

/// <summary>
/// An event one module publishes for others through its transactional outbox (ADR 0007). The event type lives in the
/// publishing module's Contracts project. <see cref="EventId"/> is the consumer's deduplication key (inbox).
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Handles one integration event type. Delivery is at least once, so a handler must be idempotent: it records the event
/// in its module's inbox in the same transaction as its own change.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
