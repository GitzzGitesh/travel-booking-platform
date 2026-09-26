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
            return Task.FromResult(false);
        }

        if (_pendingConsumed.Any(_consumed.Contains))
        {
            _pendingConsumed.Clear();
            return Task.FromResult(false); // the inbox's primary key
        }

        _consumed.UnionWith(_pendingConsumed);
        _pendingConsumed.Clear();
        Saves++;
        return Task.FromResult(true);
    }

    public Task<PaymentAttempt?> FindLiveByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.SingleOrDefault(a => a.OrderId == orderId && PaymentAttempt.LiveStatuses.Contains(a.Status)));

    public Task<IReadOnlyList<Guid>> FindReconcilableAsync(DateTimeOffset settledBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Attempts
            .Where(a => (a.Status is PaymentAttemptStatus.Authorizing or PaymentAttemptStatus.AuthorizationUnknown or PaymentAttemptStatus.ActionRequired
                    or PaymentAttemptStatus.Voiding or PaymentAttemptStatus.VoidUnknown && a.UpdatedAt <= settledBefore)
                || (a.Status == PaymentAttemptStatus.Authorized && a.ReleaseRequestedAt is not null))
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

    public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
}
