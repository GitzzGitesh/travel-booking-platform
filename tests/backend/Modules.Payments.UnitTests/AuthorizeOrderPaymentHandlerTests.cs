using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Contracts;
using TravelBooking.Modules.Payments.Domain;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>
/// Authorizing an order's total as a persisted attempt (payment-lifecycle.md; F-20, F-21, F-32): saved before the
/// provider call, one authorization per attempt, unknown outcomes looked up by our reference, never re-authorized.
/// </summary>
public sealed class AuthorizeOrderPaymentHandlerTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(15);
    private static readonly Money _total = new(270m, new CurrencyCode("XTS"));
    private static readonly Guid _orderId = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly ScriptedProvider _provider = new();

    [Fact]
    public async Task The_attempt_is_saved_before_the_provider_is_called_and_records_the_authorization()
    {
        _provider.OnAuthorize = details =>
        {
            _store.Attempts.ShouldHaveSingleItem().Status.ShouldBe(PaymentAttemptStatus.Authorizing);
            return Snapshot(details.Reference, PaymentState.Authorized);
        };

        var result = (await Handler().AuthorizeAsync(Request("key-1"), Ct)).Value;

        result.Status.ShouldBe(OrderPaymentStatus.Authorized);
        var attempt = _store.Attempts.ShouldHaveSingleItem();
        result.PaymentId.ShouldBe(attempt.Id);
        (attempt.Status, attempt.ProviderId, attempt.ProviderPaymentId).ShouldBe((PaymentAttemptStatus.Authorized, "stub", $"pi_{attempt.Reference}"));
        attempt.Events.Select(e => e.ToStatus).ShouldBe(["Authorizing", "Authorized"]);
        _provider.Authorizations.ShouldBe(1);
    }

    [Fact]
    public async Task F20_a_decline_is_final_and_replaying_the_key_returns_it_without_a_second_authorization()
    {
        _provider.OnAuthorize = details => Ok(Snapshot(details.Reference, PaymentState.Declined).Value with { DeclineReason = PaymentDeclineReason.InsufficientFunds });
        var handler = Handler();

        var first = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;
        var replay = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;

        first.ShouldBe(new OrderPaymentResult(first.PaymentId, OrderPaymentStatus.Declined, "InsufficientFunds"));
        replay.ShouldBe(first);
        (_provider.Authorizations, _provider.Lookups).ShouldBe((1, 0));
    }

    [Fact]
    public async Task F32_another_key_is_refused_while_an_attempt_may_hold_funds()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        var handler = Handler();
        await handler.AuthorizeAsync(Request("key-1"), Ct);

        var other = await handler.AuthorizeAsync(Request("key-2"), Ct);

        other.Error.ShouldBe(OrderPaymentFailure.PaymentInProgress);
        (_provider.Authorizations, _store.Attempts.Count).ShouldBe((1, 1));
    }

    [Fact]
    public async Task A_new_key_is_a_new_attempt()
    {
        _provider.OnAuthorize = details => Snapshot(details.Reference, PaymentState.Declined);
        var handler = Handler();

        var first = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;
        _provider.OnAuthorize = details => Snapshot(details.Reference, PaymentState.Authorized);
        var second = (await handler.AuthorizeAsync(Request("key-2"), Ct)).Value;

        second.PaymentId.ShouldNotBe(first.PaymentId);
        second.Status.ShouldBe(OrderPaymentStatus.Authorized);
        _store.Attempts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_authorization_is_pending_and_the_replay_looks_it_up_instead_of_authorizing_again()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "stub"));
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.Authorized).Value);
        var handler = Handler();

        var first = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;
        var replay = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;

        first.Status.ShouldBe(OrderPaymentStatus.Pending);
        replay.ShouldBe(first with { Status = OrderPaymentStatus.Authorized });
        (_provider.Authorizations, _provider.Lookups).ShouldBe((1, 1));
        _store.Attempts.Single().Events.Select(e => e.ToStatus).ShouldBe(["Authorizing", "AuthorizationUnknown", "Authorized"]);
    }

    [Fact]
    public async Task An_attempt_left_Authorizing_by_a_crash_is_looked_up_not_authorized_again()
    {
        var crashed = PaymentAttempt.Start(_orderId, "cust-1", "key-1", _total, _now);
        _store.Attempts.Add(crashed);
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.Authorized).Value);

        var result = (await Handler().AuthorizeAsync(Request("key-1"), Ct)).Value;

        result.ShouldBe(new OrderPaymentResult(crashed.Id, OrderPaymentStatus.Authorized));
        (_provider.Authorizations, _provider.Lookups).ShouldBe((0, 1));
    }

    [Fact]
    public async Task Not_found_stays_pending_inside_the_consistency_window_and_fails_after_it()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        _provider.OnLookup = _ => new PaymentLookup(null);
        var handler = Handler();
        await handler.AuthorizeAsync(Request("key-1"), Ct);

        _clock.Advance(_window - TimeSpan.FromSeconds(1));
        var early = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;
        _clock.Advance(TimeSpan.FromSeconds(1));
        var late = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;

        early.Status.ShouldBe(OrderPaymentStatus.Pending);
        late.Status.ShouldBe(OrderPaymentStatus.Failed);
        _provider.Authorizations.ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_first_call_held_nothing_but_a_rejected_lookup_proves_nothing()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "stub"));
        var rejected = (await Handler().AuthorizeAsync(Request("key-1"), Ct)).Value;

        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "stub"));
        _provider.OnLookupError = new ProviderError(ProviderErrorKind.InvalidRequest, "stub");
        await Handler().AuthorizeAsync(Request("key-2"), Ct);
        var lookedUp = (await Handler().AuthorizeAsync(Request("key-2"), Ct)).Value;

        rejected.Status.ShouldBe(OrderPaymentStatus.Failed);
        lookedUp.Status.ShouldBe(OrderPaymentStatus.Pending);
    }

    [Fact]
    public async Task F21_a_challenge_hands_the_customer_action_to_the_caller_and_the_replay_looks_it_up()
    {
        _provider.OnAuthorize = details => Ok(Snapshot(details.Reference, PaymentState.RequiresAction).Value with { CustomerActionToken = new CustomerActionToken("secret_1") });
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.Authorized).Value);
        var handler = Handler();

        var challenge = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;
        var completed = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;

        (challenge.Status, challenge.CustomerAction).ShouldBe((OrderPaymentStatus.ActionRequired, "secret_1"));
        (completed.Status, completed.CustomerAction).ShouldBe((OrderPaymentStatus.Authorized, null));
        _provider.Authorizations.ShouldBe(1);
    }

    [Fact]
    public async Task A_lookup_reporting_another_amount_goes_to_manual_review()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.Authorized).Value with { Amount = _total with { Amount = 1m } });
        var handler = Handler();
        await handler.AuthorizeAsync(Request("key-1"), Ct);

        var result = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value;

        result.Status.ShouldBe(OrderPaymentStatus.ManualReview);
    }

    [Theory]
    [InlineData(PaymentState.Captured)] // never captured by us: unexpected in the authorization phase
    [InlineData(PaymentState.Voided)]
    public async Task A_lookup_reporting_a_later_phase_goes_to_manual_review(PaymentState state)
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, state).Value);
        var handler = Handler();
        await handler.AuthorizeAsync(Request("key-1"), Ct);

        (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value.Status.ShouldBe(OrderPaymentStatus.ManualReview);
    }

    [Fact]
    public async Task Reusing_a_key_for_another_amount_or_customer_is_refused()
    {
        _provider.OnAuthorize = details => Snapshot(details.Reference, PaymentState.Authorized);
        var handler = Handler();
        await handler.AuthorizeAsync(Request("key-1"), Ct);

        (await handler.AuthorizeAsync(Request("key-1") with { Amount = _total with { Amount = 271m } }, Ct)).Error.ShouldBe(OrderPaymentFailure.IdempotencyKeyReused);
        (await handler.AuthorizeAsync(Request("key-1") with { CustomerId = "cust-2" }, Ct)).Error.ShouldBe(OrderPaymentFailure.IdempotencyKeyReused);
        _provider.Authorizations.ShouldBe(1);
    }

    [Fact]
    public async Task A_card_number_is_refused_before_anything_is_saved_or_sent()
    {
        var result = await Handler().AuthorizeAsync(Request("key-1") with { PaymentMethodToken = "4242 4242 4242 4242" }, Ct);

        result.Error.ShouldBe(OrderPaymentFailure.InvalidPaymentMethod);
        _store.Attempts.ShouldBeEmpty();
        _provider.Authorizations.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, "cust-1", "key-1")]
    [InlineData(10, " ", "key-1")]
    [InlineData(10, "cust-1", "")]
    public async Task Invalid_requests_are_refused(decimal amount, string customer, string key)
    {
        var result = await Handler().AuthorizeAsync(new OrderPaymentRequest(_orderId, customer, key, _total with { Amount = amount }, "pm_test"), Ct);

        result.Error.ShouldBe(OrderPaymentFailure.InvalidRequest);
        _store.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconciliation_resolves_an_unknown_attempt_and_leaves_final_ones_alone()
    {
        _provider.OnAuthorize = _ => Result<PaymentSnapshot, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unknown, "stub"));
        _provider.OnLookup = reference => new PaymentLookup(Snapshot(reference, PaymentState.Authorized).Value);
        var handler = Handler();
        var id = (await handler.AuthorizeAsync(Request("key-1"), Ct)).Value.PaymentId;

        (await handler.ReconcileAsync(id, Ct))!.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        (await handler.ReconcileAsync(id, Ct))!.Status.ShouldBe(PaymentAttemptStatus.Authorized);
        (await handler.ReconcileAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
        _provider.Lookups.ShouldBe(1);
    }

    private AuthorizeOrderPaymentHandler Handler() => new(
        _store,
        new PaymentOperations(_provider, _clock, NullLogger<PaymentOperations>.Instance),
        _clock,
        Options.Create(new PaymentReconciliationOptions { NotFoundConclusiveAfter = _window }));

    private static OrderPaymentRequest Request(string key) => new(_orderId, "cust-1", key, _total, "pm_test");

    private static Result<PaymentSnapshot, ProviderError> Snapshot(PaymentReference reference, PaymentState state) =>
        Result<PaymentSnapshot, ProviderError>.Success(new PaymentSnapshot(
            reference, new ProviderPaymentRef("stub", $"pi_{reference.Value}"), state, _total, _total with { Amount = 0m }, _total with { Amount = 0m }));

    private static Result<PaymentSnapshot, ProviderError> Ok(PaymentSnapshot payment) => Result<PaymentSnapshot, ProviderError>.Success(payment);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeStore : IPaymentAttemptStore
    {
        public List<PaymentAttempt> Attempts { get; } = [];

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

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FindUnresolvedAsync(DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([.. Attempts.Where(a => !a.IsFinal && a.CreatedAt < createdBefore).Take(limit).Select(a => a.Id)]);
    }

    private sealed class ScriptedProvider : IPaymentProvider
    {
        public Func<AuthorizationDetails, Result<PaymentSnapshot, ProviderError>> OnAuthorize { get; set; } = _ => throw new InvalidOperationException("No authorization expected.");

        public Func<PaymentReference, PaymentLookup> OnLookup { get; set; } = _ => throw new InvalidOperationException("No lookup expected.");

        public ProviderError? OnLookupError { get; set; }

        public int Authorizations { get; private set; }

        public int Lookups { get; private set; }

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

        public Task<Result<PaymentSnapshot, ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<PaymentSnapshot, ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<PaymentRefund, ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<RefundLookup, ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
