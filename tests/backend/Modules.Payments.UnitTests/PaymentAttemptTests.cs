using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Domain;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>The payment attempt's state machine (testing rules: every legal and illegal transition).</summary>
public sealed class PaymentAttemptTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly PaymentAttemptStatus[] _open = [PaymentAttemptStatus.ActionRequired, PaymentAttemptStatus.AuthorizationUnknown];

    private static readonly PaymentAttemptStatus[] _final =
    [
        PaymentAttemptStatus.Authorized, PaymentAttemptStatus.Declined, PaymentAttemptStatus.Canceled,
        PaymentAttemptStatus.Expired, PaymentAttemptStatus.Failed, PaymentAttemptStatus.ManualReview,
    ];

    // Reached only through the void methods, never as an authorization outcome.
    private static readonly PaymentAttemptStatus[] _voidStatuses = [PaymentAttemptStatus.Voiding, PaymentAttemptStatus.VoidUnknown, PaymentAttemptStatus.Voided];

    // Names, not the internal enum: public theory data cannot expose module internals.
    public static TheoryData<string> Targets() => [.. _open.Concat(_final).Select(s => s.ToString())];

    public static TheoryData<string, string> OpenToAny() =>
        [.. _open.SelectMany(from => Enum.GetValues<PaymentAttemptStatus>().Where(to => to != PaymentAttemptStatus.Authorizing && !_voidStatuses.Contains(to)).Select(to => (from.ToString(), to.ToString())))];

    public static TheoryData<string, string> FinalToAny() =>
        [.. _final.SelectMany(from => Enum.GetValues<PaymentAttemptStatus>().Select(to => (from.ToString(), to.ToString())))];

    [Fact]
    public void A_new_attempt_is_Authorizing_with_its_request_recorded()
    {
        var attempt = New();

        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorizing);
        attempt.IsAuthorizationSettled.ShouldBeFalse();
        attempt.Reference.ShouldBe(attempt.Id.ToString("N"));
        var created = attempt.Events.ShouldHaveSingleItem();
        (created.FromStatus, created.ToStatus, created.Actor, created.CorrelationId).ShouldBe((null, "Authorizing", "customer", "trace-1"));
    }

    [Theory]
    [InlineData("", "key-1", 10)]
    [InlineData("cust-1", " ", 10)]
    [InlineData("cust-1", "key-1", 0)]
    public void An_attempt_needs_a_customer_a_key_and_a_positive_amount(string customer, string key, decimal amount) =>
        Should.Throw<ArgumentException>(() => PaymentAttempt.Start(Guid.NewGuid(), customer, key, new Money(amount, new CurrencyCode("XTS")), At(_now)));

    [Theory]
    [MemberData(nameof(Targets))]
    public void Authorizing_moves_to_any_outcome_with_a_history_entry(string target)
    {
        var to = Enum.Parse<PaymentAttemptStatus>(target);
        var attempt = New();

        attempt.Resolve(to, "outcome", At(_now.AddSeconds(1), "trace-2"), "mockpay", "pay_1").Value.ShouldBe(to);

        attempt.Status.ShouldBe(to);
        attempt.ProviderPaymentId.ShouldBe("pay_1");
        var entry = attempt.Events[^1];
        (entry.FromStatus, entry.ToStatus, entry.Reason, entry.ProviderReference, entry.CorrelationId).ShouldBe(("Authorizing", to.ToString(), "outcome", "pay_1", "trace-2"));
        attempt.UpdatedAt.ShouldBe(_now.AddSeconds(1));
    }

    [Theory]
    [MemberData(nameof(OpenToAny))]
    public void A_challenge_or_an_unknown_outcome_is_resolved_later(string source, string target)
    {
        var (from, to) = (Enum.Parse<PaymentAttemptStatus>(source), Enum.Parse<PaymentAttemptStatus>(target));
        var attempt = New();
        attempt.Resolve(from, "first", At(_now));
        var revision = attempt.Revision;

        attempt.Resolve(to, "later", At(_now.AddMinutes(1))).IsSuccess.ShouldBeTrue();

        attempt.Status.ShouldBe(to);
        attempt.Revision.ShouldBeGreaterThan(revision);
        attempt.Events.Count.ShouldBe(to == from ? 2 : 3); // the same status again adds no history entry
    }

    [Theory]
    [MemberData(nameof(FinalToAny))]
    public void A_final_attempt_never_changes(string source, string target)
    {
        var (from, to) = (Enum.Parse<PaymentAttemptStatus>(source), Enum.Parse<PaymentAttemptStatus>(target));
        var attempt = New();
        attempt.Resolve(from, "final", At(_now), "mockpay", "pay_1");
        var (revision, events) = (attempt.Revision, attempt.Events.Count);

        attempt.Resolve(to, "late", At(_now.AddMinutes(1)), "mockpay", "pay_2").Error.ShouldBe(PaymentAttemptTransitionError.AlreadyFinal);

        attempt.Status.ShouldBe(from);
        attempt.ProviderPaymentId.ShouldBe("pay_1");
        (attempt.Revision, attempt.Events.Count).ShouldBe((revision, events));
    }

    [Theory]
    [InlineData("Authorizing")]
    [InlineData("ActionRequired")]
    [InlineData("AuthorizationUnknown")]
    public void Nothing_goes_back_to_Authorizing(string source)
    {
        var from = Enum.Parse<PaymentAttemptStatus>(source);
        var attempt = New();
        attempt.Resolve(from, "first", At(_now));

        attempt.Resolve(PaymentAttemptStatus.Authorizing, "back", At(_now)).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);

        attempt.Status.ShouldBe(from);
    }

    [Fact]
    public void The_same_unknown_status_again_fills_in_provider_details_without_new_history()
    {
        var attempt = New();
        attempt.Resolve(PaymentAttemptStatus.ActionRequired, "challenge", At(_now));

        attempt.Resolve(PaymentAttemptStatus.ActionRequired, "challenge", At(_now.AddSeconds(5)), "mockpay", "pay_1").IsSuccess.ShouldBeTrue();

        attempt.ProviderPaymentId.ShouldBe("pay_1");
        attempt.Events.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Voiding")]
    [InlineData("VoidUnknown")]
    [InlineData("Voided")]
    public void An_authorization_outcome_is_never_a_void_status(string target)
    {
        var attempt = New();

        attempt.Resolve(Enum.Parse<PaymentAttemptStatus>(target), "no", At(_now)).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);

        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorizing);
    }

    [Fact]
    public void A_release_request_is_recorded_once_without_changing_the_status()
    {
        var attempt = Authorized();

        attempt.RequestRelease("the offer expired before booking", At(_now.AddMinutes(1))).ShouldBeTrue();
        attempt.RequestRelease("again", At(_now.AddMinutes(2))).ShouldBeTrue();

        (attempt.Status, attempt.ReleaseRequestedAt, attempt.ReleaseReason).ShouldBe((PaymentAttemptStatus.Authorized, _now.AddMinutes(1), "the offer expired before booking"));
        var entry = attempt.Events.Where(e => e.Reason.StartsWith("Release requested", StringComparison.Ordinal)).ShouldHaveSingleItem();
        (entry.FromStatus, entry.ToStatus).ShouldBe(("Authorized", "Authorized"));
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public void Nothing_is_released_from_an_attempt_that_holds_nothing(string status)
    {
        var attempt = New();
        attempt.Resolve(Enum.Parse<PaymentAttemptStatus>(status), "final", At(_now));

        attempt.RequestRelease("expired", At(_now)).ShouldBeFalse();

        attempt.ReleaseRequestedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("Voided")]
    [InlineData("Canceled")]
    [InlineData("VoidUnknown")]
    [InlineData("ManualReview")]
    public void A_void_is_saved_as_Voiding_then_resolved(string outcome)
    {
        var attempt = Authorized();

        attempt.BeginVoid(At(_now.AddMinutes(1))).Value.ShouldBe(PaymentAttemptStatus.Voiding);
        attempt.ResolveVoid(Enum.Parse<PaymentAttemptStatus>(outcome), "void outcome", At(_now.AddMinutes(2))).IsSuccess.ShouldBeTrue();

        attempt.Events.Select(e => e.ToStatus).TakeLast(2).ShouldBe(["Voiding", outcome]);
        (attempt.Events[^2].ProviderReference, attempt.Events[^2].Actor).ShouldBe(("pay_1", "customer"));
    }

    [Fact]
    public void A_void_with_an_unknown_outcome_can_be_repeated_and_a_void_in_progress_is_not_restarted()
    {
        var attempt = Authorized();
        attempt.BeginVoid(At(_now));
        attempt.BeginVoid(At(_now)).IsSuccess.ShouldBeTrue(); // already voiding: a no-op
        attempt.ResolveVoid(PaymentAttemptStatus.VoidUnknown, "timeout", At(_now));

        attempt.BeginVoid(At(_now.AddMinutes(1))).Value.ShouldBe(PaymentAttemptStatus.Voiding);
        attempt.ResolveVoid(PaymentAttemptStatus.Voided, "voided", At(_now.AddMinutes(1)));

        attempt.Status.ShouldBe(PaymentAttemptStatus.Voided);
        attempt.Events.Count(e => e.ToStatus == "Voiding").ShouldBe(2);
    }

    [Fact]
    public void An_unfinished_challenge_can_be_voided()
    {
        var attempt = New();
        attempt.Resolve(PaymentAttemptStatus.ActionRequired, "challenge", At(_now), "mockpay", "pay_1");

        attempt.BeginVoid(At(_now)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Declined", "Illegal")]
    [InlineData("Failed", "Illegal")]
    [InlineData("AuthorizationUnknown", "Illegal")] // look it up first: void only a known hold
    [InlineData("Voided", "AlreadyFinal")]
    [InlineData("Canceled", "AlreadyFinal")]
    public void Only_a_known_hold_is_voided(string status, string error)
    {
        var attempt = Authorized();
        if (status is "Voided")
        {
            attempt.BeginVoid(At(_now));
            attempt.ResolveVoid(PaymentAttemptStatus.Voided, "voided", At(_now));
        }
        else
        {
            attempt = New();
            attempt.Resolve(Enum.Parse<PaymentAttemptStatus>(status), "outcome", At(_now), "mockpay", "pay_1");
        }

        attempt.BeginVoid(At(_now)).Error.ShouldBe(Enum.Parse<PaymentAttemptTransitionError>(error));
    }

    [Fact]
    public void A_void_is_resolved_only_while_one_is_in_progress_and_only_to_a_void_outcome()
    {
        var attempt = Authorized();

        attempt.ResolveVoid(PaymentAttemptStatus.Voided, "no void started", At(_now)).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);
        attempt.BeginVoid(At(_now));
        attempt.ResolveVoid(PaymentAttemptStatus.Authorized, "not a void outcome", At(_now)).Error.ShouldBe(PaymentAttemptTransitionError.Illegal);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Voiding);
    }

    [Fact]
    public void A_void_in_progress_still_counts_as_holding_funds()
    {
        PaymentAttempt.LiveStatuses.ShouldContain(PaymentAttemptStatus.Voiding);
        PaymentAttempt.LiveStatuses.ShouldContain(PaymentAttemptStatus.VoidUnknown);
        PaymentAttempt.LiveStatuses.ShouldNotContain(PaymentAttemptStatus.Voided);
    }

    private static PaymentAttempt Authorized()
    {
        var attempt = New();
        attempt.Resolve(PaymentAttemptStatus.Authorized, "authorized", At(_now), "mockpay", "pay_1");
        return attempt;
    }

    private static PaymentChange At(DateTimeOffset at, string correlationId = "trace-1") => new(at, "customer", correlationId);

    private static PaymentAttempt New() =>
        PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-1", new Money(270m, new CurrencyCode("XTS")), At(_now));
}
