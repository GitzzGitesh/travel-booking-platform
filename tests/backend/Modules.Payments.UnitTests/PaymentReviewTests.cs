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
/// The way out of ManualReview: only to what a lookup at the provider establishes, idempotently, and recorded on the
/// attempt's history with the operator and their reason.
/// </summary>
public sealed class PaymentReviewTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Money _total = new(270m, new CurrencyCode("XTS"));
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(15);

    private readonly FakeTimeProvider _clock = new(_now);
    private readonly FakeStore _store = new();
    private readonly ScriptedProvider _provider = new();

    [Theory]
    [InlineData(PaymentState.Voided, PaymentAttemptStatus.Voided)]
    [InlineData(PaymentState.Canceled, PaymentAttemptStatus.Canceled)]
    [InlineData(PaymentState.Declined, PaymentAttemptStatus.Declined)]
    [InlineData(PaymentState.Expired, PaymentAttemptStatus.Expired)]
    [InlineData(PaymentState.Authorized, PaymentAttemptStatus.Authorized)] // the hold is real: released or used as usual
    internal async Task A_review_resolves_to_what_the_provider_holds_with_the_operator_on_the_history(PaymentState found, PaymentAttemptStatus expected)
    {
        var attempt = await InReview();
        _provider.OnLookup = reference => Found(reference, found, _total);

        var result = await Resolve(attempt.Id);

        result.ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.Resolved, expected));
        attempt.Status.ShouldBe(expected);
        var entry = attempt.Events[^1];
        (entry.FromStatus, entry.ToStatus, entry.Actor, entry.CorrelationId).ShouldBe(("ManualReview", expected.ToString(), "operator:ops-1", "trace-ops"));
        entry.Reason.ShouldContain("checked with the provider on ticket 42");
    }

    [Fact]
    public async Task Resolving_again_changes_nothing_and_asks_the_provider_nothing()
    {
        var attempt = await InReview();
        _provider.OnLookup = reference => Found(reference, PaymentState.Voided, _total);
        await Resolve(attempt.Id);
        var events = attempt.Events.Count;
        var lookups = _provider.Lookups;

        var again = await Resolve(attempt.Id);

        again.ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.AlreadyResolved, PaymentAttemptStatus.Voided));
        (attempt.Events.Count, _provider.Lookups).ShouldBe((events, lookups));
    }

    [Fact]
    public async Task What_the_provider_does_not_settle_stays_in_review_and_the_check_is_recorded()
    {
        var captured = await InReview();
        var otherAmount = await InReview();
        var failing = await InReview();

        _provider.OnLookup = reference => Found(reference, PaymentState.Captured, _total);
        var afterCapture = await Resolve(captured.Id);
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized, _total with { Amount = 300m });
        var afterOtherAmount = await Resolve(otherAmount.Id);
        _provider.OnLookupError = new ProviderError(ProviderErrorKind.Unavailable, "down");
        var afterFailure = await Resolve(failing.Id);

        new[] { afterCapture, afterOtherAmount, afterFailure }.ShouldAllBe(r => r == new PaymentReviewResult(PaymentReviewOutcome.StillNeedsReview, PaymentAttemptStatus.ManualReview));
        captured.Events[^1].Reason.ShouldContain("still in review");
        captured.Events[^1].Actor.ShouldBe("operator:ops-1");
    }

    [Fact]
    public async Task Only_an_attempt_in_review_is_resolved_and_the_request_must_name_its_operator_and_reason()
    {
        _provider.OnAuthorize = details => Result<PaymentSnapshot, ProviderError>.Success(Found(details.Reference, PaymentState.Declined, _total).Payment!);
        var declined = await Authorize();

        (await Resolve(declined.Id)).ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.AlreadyResolved, PaymentAttemptStatus.Declined));
        (await Resolve(Guid.NewGuid())).Outcome.ShouldBe(PaymentReviewOutcome.NotFound);
        (await Handler().HandleAsync(new ResolvePaymentReview(declined.Id, "", "reason"), Ct)).Outcome.ShouldBe(PaymentReviewOutcome.Invalid);
        (await Handler().HandleAsync(new ResolvePaymentReview(declined.Id, "ops 1", "reason"), Ct)).Outcome.ShouldBe(PaymentReviewOutcome.Invalid);
        (await Handler().HandleAsync(new ResolvePaymentReview(declined.Id, "ops-1", " "), Ct)).Outcome.ShouldBe(PaymentReviewOutcome.Invalid);
        (await Handler().HandleAsync(new ResolvePaymentReview(declined.Id, "ops-1", "line\nbreak"), Ct)).Outcome.ShouldBe(PaymentReviewOutcome.Invalid);
        (await Handler().HandleAsync(new ResolvePaymentReview(declined.Id, "ops-1", new string('r', 201)), Ct)).Outcome.ShouldBe(PaymentReviewOutcome.Invalid);
        _provider.Lookups.ShouldBe(0);
    }

    [Fact]
    public async Task A_payment_the_provider_no_longer_finds_stays_in_review_even_after_the_window()
    {
        var attempt = await InReview(); // the provider acknowledged it: its payment id is known
        attempt.ProviderPaymentId.ShouldNotBeNull();
        _provider.OnLookup = _ => new PaymentLookup(null);
        _clock.Advance(_window + TimeSpan.FromMinutes(1));

        (await Resolve(attempt.Id)).ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.StillNeedsReview, PaymentAttemptStatus.ManualReview));
    }

    [Fact]
    public async Task A_payment_the_provider_never_acknowledged_fails_only_after_the_window()
    {
        var attempt = await ReviewWithoutProviderId();
        _provider.OnLookup = _ => new PaymentLookup(null);

        var early = await Resolve(attempt.Id);
        _clock.Advance(_window + TimeSpan.FromMinutes(1));
        var late = await Resolve(attempt.Id);

        early.Outcome.ShouldBe(PaymentReviewOutcome.StillNeedsReview);
        late.ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.Resolved, PaymentAttemptStatus.Failed));
    }

    [Fact]
    public async Task A_hold_found_by_the_review_is_recorded_with_its_provider_payment_so_a_release_can_void_it()
    {
        var attempt = await ReviewWithoutProviderId();
        _provider.OnLookup = reference => Found(reference, PaymentState.Authorized, _total);

        (await Resolve(attempt.Id)).ShouldBe(new PaymentReviewResult(PaymentReviewOutcome.Resolved, PaymentAttemptStatus.Authorized));

        attempt.ProviderPaymentId.ShouldBe($"pi_{attempt.Reference}");
        attempt.VoidKey.ShouldBe($"{attempt.Reference}:void-1"); // a fresh void, never a replay of one refused before
        attempt.RequestRelease("offer expired", new PaymentChange(_now, "system:orders", null));
        attempt.BeginVoid(new PaymentChange(_now, "system:payment-reconciliation", null)).Value.ShouldBe(PaymentAttemptStatus.Voiding);
    }

    [Fact]
    public async Task The_domain_moves_out_of_review_only_to_a_provider_established_status()
    {
        var attempt = await InReview();
        var change = new PaymentChange(_now, "operator:ops-1", null);

        attempt.ResolveReview(PaymentAttemptStatus.ActionRequired, "no", change).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);
        attempt.ResolveReview(PaymentAttemptStatus.Voiding, "no", change).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);
        attempt.ResolveReview(PaymentAttemptStatus.Canceled, "yes", change).Value.ShouldBe(PaymentAttemptStatus.Canceled);
        attempt.ResolveReview(PaymentAttemptStatus.Voided, "late", change).Error.ShouldBe(PaymentAttemptTransitionError.AlreadyFinal);
    }

    // A provider reporting another amount than asked puts the attempt in ManualReview.
    private async Task<PaymentAttempt> InReview()
    {
        _provider.OnAuthorize = details => Result<PaymentSnapshot, ProviderError>.Success(Found(details.Reference, PaymentState.Authorized, _total with { Amount = 999m }).Payment!);
        var attempt = await Authorize();
        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
        return attempt;
    }

    // A provider answer without a snapshot (a capture during authorization) puts it in review with no provider payment id.
    private async Task<PaymentAttempt> ReviewWithoutProviderId()
    {
        _provider.OnAuthorize = details => Result<PaymentSnapshot, ProviderError>.Success(Found(details.Reference, PaymentState.Captured, _total).Payment!);
        var attempt = await Authorize();
        attempt.Status.ShouldBe(PaymentAttemptStatus.ManualReview);
        attempt.ProviderPaymentId.ShouldBeNull();
        return attempt;
    }

    private async Task<PaymentAttempt> Authorize()
    {
        var handler = new AuthorizeOrderPaymentHandler(_store, Operations(), _clock, Options.Create(new PaymentReconciliationOptions { NotFoundConclusiveAfter = _window }));
        var result = await handler.AuthorizeAsync(new OrderPaymentRequest(Guid.NewGuid(), "cust-1", $"key-{Guid.NewGuid():N}", _total, "pm_test"), Ct);
        return _store.Attempts.Single(a => a.Id == result.Value.PaymentId);
    }

    private Task<PaymentReviewResult> Resolve(Guid attemptId) =>
        Handler().HandleAsync(new ResolvePaymentReview(attemptId, "ops-1", "checked with the provider on ticket 42", "trace-ops"), Ct);

    private ResolvePaymentReviewHandler Handler() =>
        new(_store, Operations(), _clock, Options.Create(new PaymentReconciliationOptions { NotFoundConclusiveAfter = _window }));

    private PaymentOperations Operations() => new(_provider, _clock, NullLogger<PaymentOperations>.Instance);

    private static PaymentLookup Found(PaymentReference reference, PaymentState state, Money amount) => new(new PaymentSnapshot(
        reference, new ProviderPaymentRef("stub", $"pi_{reference.Value}"), state, amount,
        state is PaymentState.Captured ? amount : amount with { Amount = 0m }, amount with { Amount = 0m }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
