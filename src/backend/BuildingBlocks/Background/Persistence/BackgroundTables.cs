using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TravelBooking.BuildingBlocks.Background.Persistence;

/// <summary>
/// An integration event waiting to be dispatched, written in the same transaction as the state change that raised it
/// (ADR 0007). Dispatch is at least once; the consumer's inbox removes duplicates.
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxTypeLength = 200;

    private OutboxMessage()
    {
    }

    /// <summary>The event's own id: the consumers' deduplication key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The event's type name (its CLR full name), resolved by the dispatcher's registry.</summary>
    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>The W3C trace context of the request that raised the event, so traces continue across the async hop.</summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>The exception type of the last failed attempt (never its message: it may carry data).</summary>
    public string? LastError { get; private set; }

    /// <summary>Set when the message is given up on after too many attempts: an operational alert, never silent.</summary>
    public DateTimeOffset? FailedAt { get; private set; }

    public static OutboxMessage From<TEvent>(TEvent integrationEvent, string? correlationId)
        where TEvent : IIntegrationEvent => new()
        {
            Id = integrationEvent.EventId,
            Type = typeof(TEvent).FullName!,
            Payload = JsonSerializer.Serialize(integrationEvent, JsonSerializerOptions.Web),
            OccurredAt = integrationEvent.OccurredAt,
            NextAttemptAt = integrationEvent.OccurredAt,
            CorrelationId = correlationId,
            TraceParent = Activity.Current?.Id,
        };

    internal void MarkProcessed(DateTimeOffset at)
    {
        ProcessedAt = at;
        LastError = null;
    }

    internal void MarkAttemptFailed(DateTimeOffset at, string error, int maxAttempts)
    {
        Attempts++;
        LastError = error;

        // Exponential back-off, capped at five minutes.
        NextAttemptAt = at + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Attempts)));
        if (Attempts >= maxAttempts)
        {
            FailedAt = at;
        }
    }
}

/// <summary>An integration event a handler has already consumed (ADR 0007): the unique key makes a redelivery a no-op.</summary>
public sealed class InboxMessage
{
    public const int MaxHandlerLength = 200;

    private InboxMessage()
    {
    }

    public Guid MessageId { get; private set; }

    public string Handler { get; private set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; private set; }

    public static InboxMessage For(Guid messageId, string handler, DateTimeOffset processedAt) =>
        new() { MessageId = messageId, Handler = handler, ProcessedAt = processedAt };
}

/// <summary>Who may run a named job until when (ADR 0007: a DB lease, so several Worker processes never overlap).</summary>
public sealed class JobLease
{
    public const int MaxNameLength = 100;

    private JobLease()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string Owner { get; private set; } = string.Empty;

    public DateTimeOffset LeaseUntil { get; private set; }

    internal static JobLease Take(string name, string owner, DateTimeOffset until) => new() { Name = name, Owner = owner, LeaseUntil = until };
}

/// <summary>Maps the background-processing tables into a module's own schema (its default schema).</summary>
public static class BackgroundModelBuilderExtensions
{
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(m => m.Id);
        outbox.Property(m => m.Id).ValueGeneratedNever();
        outbox.Property(m => m.Type).HasMaxLength(OutboxMessage.MaxTypeLength).IsUnicode(false);
        outbox.Property(m => m.CorrelationId).HasMaxLength(100);
        outbox.Property(m => m.TraceParent).HasMaxLength(100).IsUnicode(false);
        outbox.Property(m => m.LastError).HasMaxLength(200);

        // The dispatcher's work list: pending messages in order.
        outbox.HasIndex(m => new { m.NextAttemptAt, m.OccurredAt }).HasFilter("[ProcessedAt] IS NULL AND [FailedAt] IS NULL");
        return modelBuilder;
    }

    public static ModelBuilder AddInbox(this ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("InboxMessages");
        inbox.HasKey(m => new { m.MessageId, m.Handler });
        inbox.Property(m => m.Handler).HasMaxLength(InboxMessage.MaxHandlerLength).IsUnicode(false);
        return modelBuilder;
    }

    public static ModelBuilder AddJobLeases(this ModelBuilder modelBuilder)
    {
        var lease = modelBuilder.Entity<JobLease>();
        lease.ToTable("JobLeases");
        lease.HasKey(l => l.Name);
        lease.Property(l => l.Name).HasMaxLength(JobLease.MaxNameLength).IsUnicode(false);
        lease.Property(l => l.Owner).HasMaxLength(200);
        return modelBuilder;
    }
}
