using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>An in-memory attempt store with the database's unique rules (key per order, one live attempt, inbox).</summary>
internal sealed class FakeStore : IPaymentAttemptStore
{
    private readonly HashSet<(Guid, string)> _consumed = [];
    private readonly List<(Guid, string)> _pendingConsumed = [];

    public List<PaymentAttempt> Attempts { get; } = [];

    public int Saves { get; private set; }

    /// <summary>The next save fails as a concurrency conflict (another writer won).</summary>
    public bool FailNextSave { get; set; }

    public int Consumed => _consumed.Count;

    public Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.SingleOrDefault(a => a.Id == attemptId));

    public Task<PaymentAttempt?> FindByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.SingleOrDefault(a => a.OrderId == orderId && a.IdempotencyKey == idempotencyKey));

    public Task<bool> TryAddAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        if (Attempts.Any(a => a.OrderId == attempt.OrderId && (a.IdempotencyKey == attempt.IdempotencyKey || PaymentAttempt.LiveStatuses.Contains(a.Status))))
        {
            return Task.FromResult(false);
        }

        Attempts.Add(attempt);
        return Task.FromResult(true);
    }

    public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            _pendingConsumed.Clear();
            _pendingTrip = null;
            return Task.FromResult(false);
        }

        if (_pendingConsumed.Any(_consumed.Contains))
        {
            _pendingConsumed.Clear();
            return Task.FromResult(false); // the inbox's primary key
        }

        if (_pendingTrip is { } trip)
        {
            _pendingTrip = null;
            if (Trips.Any(t => t.CustomerId == trip.CustomerId && t.IdempotencyKey == trip.IdempotencyKey))
            {
                return Task.FromResult(false); // the trips' unique key
            }

            Trips.Add(trip);
        }

        _consumed.UnionWith(_pendingConsumed);
        _pendingConsumed.Clear();
        Saves++;
        return Task.FromResult(true);
    }

    public List<TravelBooking.BuildingBlocks.Audit.AuditEntry> AuditEntries { get; } = [];

    public void Audit(TravelBooking.BuildingBlocks.Audit.AuditEntry entry) => AuditEntries.Add(entry);

    public List<AttemptLimitTrip> Trips { get; } = [];

    private AttemptLimitTrip? _pendingTrip;

    public void RecordLimitTrip(AttemptLimitTrip trip) => _pendingTrip = trip;

    public Task<int> CountLimitTripsSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(Trips.Count(t => t.CustomerId == customerId && t.At >= since));

    public Task<IReadOnlyList<CustomerTrips>> FindCustomersToReviewAsync(DateTimeOffset since, int minimumTrips, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerTrips>>([.. Trips.Where(t => t.At >= since).GroupBy(t => t.CustomerId)
            .Where(g => g.Count() >= minimumTrips).Select(g => new CustomerTrips(g.Key, g.Count()))]);

    public Task<int> CountForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.Count(a => a.OrderId == orderId));

    public Task<int> CountForCustomerSinceAsync(string customerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.Count(a => a.CustomerId == customerId && a.CreatedAt >= since));

    public Task<PaymentAttempt?> FindLiveByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.SingleOrDefault(a => a.OrderId == orderId && PaymentAttempt.LiveStatuses.Contains(a.Status)));

    public List<RefundRecord> Refunds { get; } = [];

    public List<TravelBooking.BuildingBlocks.Background.IIntegrationEvent> Published { get; } = [];

    public void AddRefund(RefundRecord refund) => Refunds.Add(refund);

    public Task<RefundRecord?> FindRefundAsync(Guid refundId, CancellationToken cancellationToken) => Task.FromResult(Refunds.SingleOrDefault(r => r.Id == refundId));

    public Task<IReadOnlyList<RefundRecord>> FindRefundsAsync(Guid attemptId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefundRecord>>([.. Refunds.Where(r => r.AttemptId == attemptId)]);

    public Task<PaymentAttempt?> FindCapturedByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.FirstOrDefault(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.Captured));

    public Task<IReadOnlyList<Guid>> FindRefundsToProcessAsync(DateTimeOffset interruptedBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Refunds
            .Where(r => r.Status is RefundRecordStatus.Requested or RefundRecordStatus.Pending or RefundRecordStatus.Unknown
                || (r.Status is RefundRecordStatus.Refunding && r.UpdatedAt <= interruptedBefore))
            .OrderBy(r => r.UpdatedAt).Select(r => r.Id).Take(limit)]);

    public void Publish<TEvent>(TEvent integrationEvent, string? correlationId)
        where TEvent : TravelBooking.BuildingBlocks.Background.IIntegrationEvent => Published.Add(integrationEvent);

    public Task<IReadOnlyList<Guid>> FindHoldsToWarnAsync(DateTimeOffset authorizedBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Attempts
            .Where(a => a.MayHoldFunds && a.HoldStartedAt <= authorizedBefore && a.HoldWarningRaisedAt is null)
            .OrderBy(a => a.HoldStartedAt).Select(a => a.Id).Take(limit)]);

    public Task<IReadOnlyList<Guid>> FindReconcilableAsync(DateTimeOffset settledBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Attempts
            .Where(a => (a.Status is PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown or PaymentAttemptStatus.ActionRequired
                    or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown or PaymentAttemptStatus.Capturing or PaymentAttemptStatus.CaptureUnknown
                    && a.UpdatedAt <= settledBefore)
                || (a.Status == PaymentAttemptStatus.Authorized && (a.ReleaseRequestedAt is not null || a.CaptureRequestedAt is not null)))
            .OrderBy(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => a.Id)]);

    public Task<bool> HasConsumedAsync(Guid messageId, string handler, CancellationToken cancellationToken) =>
        Task.FromResult(_consumed.Contains((messageId, handler)));

    public void MarkConsumed(Guid messageId, string handler, DateTimeOffset at) => _pendingConsumed.Add((messageId, handler));
}

/// <summary>A payment provider that answers what each test scripts, and counts every write.</summary>
internal sealed class ScriptedProvider : IPaymentProvider
{
    public Func<AuthorizationDetails, Result<PaymentSnapshot, ProviderError>> OnAuthorize { get; set; } = _ => throw new InvalidOperationException("No authorization expected.");

    public Func<PaymentReference, PaymentLookup> OnLookup { get; set; } = _ => throw new InvalidOperationException("No lookup expected.");

    public Func<VoidDetails, Result<PaymentSnapshot, ProviderError>> OnVoid { get; set; } = _ => throw new InvalidOperationException("No void expected.");

    public ProviderError? OnLookupError { get; set; }

    public int Authorizations { get; private set; }

    public int Lookups { get; private set; }

    public List<VoidDetails> Voids { get; } = [];

    public Func<CaptureDetails, Result<PaymentSnapshot, ProviderError>> OnCapture { get; set; } = _ => throw new InvalidOperationException("No capture expected.");

    public List<CaptureDetails> Captures { get; } = [];

    public string Id => "stub";

    public Task<Result<PaymentSnapshot, ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken)
    {
        Authorizations++;
        return Task.FromResult(OnAuthorize(details));
    }

    public Task<Result<PaymentLookup, ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken)
    {
        Lookups++;
        return Task.FromResult(OnLookupError is { } error
            ? Result<PaymentLookup, ProviderError>.Failure(error)
            : Result<PaymentLookup, ProviderError>.Success(OnLookup(reference)));
    }

    public Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken)
    {
        Voids.Add(details);
        return Task.FromResult(OnVoid(details));
    }

    public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken)
    {
        Captures.Add(details);
        return Task.FromResult(OnCapture(details));
    }

    public Func<RefundDetails, Result<PaymentRefund, ProviderError>> OnRefund { get; set; } = _ => throw new InvalidOperationException("No refund expected.");

    public Func<OperationKey, RefundLookup> OnRefundLookup { get; set; } = _ => throw new InvalidOperationException("No refund lookup expected.");

    public List<RefundDetails> RefundsSent { get; } = [];

    public int RefundLookups { get; private set; }

    public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken)
    {
        RefundsSent.Add(details);
        return Task.FromResult(OnRefund(details));
    }

    public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken)
    {
        RefundLookups++;
        return Task.FromResult(Result<RefundLookup, ProviderError>.Success(OnRefundLookup(key)));
    }
}
