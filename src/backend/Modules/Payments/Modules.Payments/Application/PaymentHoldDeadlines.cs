using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Background;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>
/// <c>Payments:Holds</c> (ADR 0025): how long the provider keeps an authorization (the hold) before it lapses, and how
/// early operations are warned. The lifetime is a placeholder until the payment provider confirms it for our account
/// (cards are commonly about 7 days); it is configuration, never assumed in code.
/// </summary>
internal sealed class PaymentHoldOptions
{
    public const string SectionName = "Payments:Holds";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan WarningBefore { get; set; } = TimeSpan.FromHours(48);

    public bool IsValid() => Lifetime > TimeSpan.Zero && WarningBefore > TimeSpan.Zero && WarningBefore < Lifetime;

    public DateTimeOffset ExpiresAt(PaymentAttempt attempt) => attempt.HoldStartedAt + Lifetime;
}

/// <summary>
/// The Worker's watch over held funds (ADR 0025): a hold that is neither captured nor released when its warning time
/// comes raises <c>PaymentHoldExpiring</c> once, so a person settles it (capture, release or the review) before it lapses.
/// It never captures or releases anything itself.
/// </summary>
internal sealed partial class WatchExpiringHoldsJob(
    IPaymentAttemptStore store, IOptions<PaymentHoldOptions> holds, TimeProvider timeProvider, ILogger<WatchExpiringHoldsJob> logger) : IBackgroundJob
{
    public const string Name = "payments.watch-expiring-holds";
    public const int BatchSize = 50;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var authorizedBefore = now - (holds.Value.Lifetime - holds.Value.WarningBefore);
        var warned = 0;
        foreach (var id in await store.FindHoldsToWarnAsync(authorizedBefore, BatchSize, cancellationToken))
        {
            // Each attempt on its own: a concurrent change makes this one wait for the next run, never the others.
            if (await store.FindAsync(id, cancellationToken) is { } attempt && attempt.NoteHoldExpiring(now) && await store.TrySaveAsync(cancellationToken))
            {
                LogHoldExpiring(logger, attempt.Id, attempt.OrderId, attempt.Status.ToString(), holds.Value.ExpiresAt(attempt));
                warned++;
            }
        }

        return warned;
    }

    [LoggerMessage(Level = LogLevel.Error, EventName = "PaymentHoldExpiring",
        Message = "Alert: the payment hold of attempt {AttemptId} (order {OrderId}, {Status}) lapses at {ExpiresAt}: capture, release or resolve it first")]
    private static partial void LogHoldExpiring(ILogger logger, Guid attemptId, Guid orderId, string status, DateTimeOffset expiresAt);
}
