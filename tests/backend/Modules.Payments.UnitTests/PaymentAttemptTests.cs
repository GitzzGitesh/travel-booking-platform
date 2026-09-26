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

    // Names, not the internal enum: public theory data cannot expose module internals.
    public static TheoryData<string> Targets() => [.. _open.Concat(_final).Select(s => s.ToString())];

    public static TheoryData<string, string> OpenToAny() =>
        [.. _open.SelectMany(from => Enum.GetValues<PaymentAttemptStatus>().Where(to => to != PaymentAttemptStatus.Authorizing).Select(to => (from.ToString(), to.ToString())))];

    public static TheoryData<string, string> FinalToAny() =>
        [.. _final.SelectMany(from => Enum.GetValues<PaymentAttemptStatus>().Select(to => (from.ToString(), to.ToString())))];

    [Fact]
    public void A_new_attempt_is_Authorizing_with_its_request_recorded()
    {
        var attempt = New();

        attempt.Status.ShouldBe(PaymentAttemptStatus.Authorizing);
        attempt.IsFinal.ShouldBeFalse();
        attempt.Reference.ShouldBe(attempt.Id.ToString("N"));
        var created = attempt.Events.ShouldHaveSingleItem();
        created.FromStatus.ShouldBeNull();
        created.ToStatus.ShouldBe("Authorizing");
    }

    [Theory]
    [InlineData("", "key-1", 10)]
    [InlineData("cust-1", " ", 10)]
    [InlineData("cust-1", "key-1", 0)]
    public void An_attempt_needs_a_customer_a_key_and_a_positive_amount(string customer, string key, decimal amount) =>
        Should.Throw<ArgumentException>(() => PaymentAttempt.Start(Guid.NewGuid(), customer, key, new Money(amount, new CurrencyCode("XTS")), _now));

    [Theory]
    [MemberData(nameof(Targets))]
    public void Authorizing_moves_to_any_outcome_with_a_history_entry(string target)
    {
        var to = Enum.Parse<PaymentAttemptStatus>(target);
        var attempt = New();

        attempt.Resolve(to, "outcome", _now.AddSeconds(1), "mockpay", "pay_1").ShouldBeTrue();

        attempt.Status.ShouldBe(to);
        attempt.ProviderPaymentId.ShouldBe("pay_1");
        var entry = attempt.Events[^1];
        (entry.FromStatus, entry.ToStatus, entry.Reason, entry.ProviderReference).ShouldBe(("Authorizing", to.ToString(), "outcome", "pay_1"));
        attempt.UpdatedAt.ShouldBe(_now.AddSeconds(1));
    }

    [Theory]
    [MemberData(nameof(OpenToAny))]
    public void A_challenge_or_an_unknown_outcome_is_resolved_later(string source, string target)
    {
        var (from, to) = (Enum.Parse<PaymentAttemptStatus>(source), Enum.Parse<PaymentAttemptStatus>(target));
        var attempt = New();
        attempt.Resolve(from, "first", _now);
        var revision = attempt.Revision;

        attempt.Resolve(to, "later", _now.AddMinutes(1)).ShouldBeTrue();

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
        attempt.Resolve(from, "final", _now, "mockpay", "pay_1");
        var (revision, events) = (attempt.Revision, attempt.Events.Count);

        attempt.Resolve(to, "late", _now.AddMinutes(1), "mockpay", "pay_2").ShouldBeFalse();

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
        attempt.Resolve(from, "first", _now);

        attempt.Resolve(PaymentAttemptStatus.Authorizing, "back", _now).ShouldBeFalse();

        attempt.Status.ShouldBe(from);
    }

    [Fact]
    public void The_same_unknown_status_again_fills_in_provider_details_without_new_history()
    {
        var attempt = New();
        attempt.Resolve(PaymentAttemptStatus.ActionRequired, "challenge", _now);

        attempt.Resolve(PaymentAttemptStatus.ActionRequired, "challenge", _now.AddSeconds(5), "mockpay", "pay_1").ShouldBeTrue();

        attempt.ProviderPaymentId.ShouldBe("pay_1");
        attempt.Events.Count.ShouldBe(2);
    }

    private static PaymentAttempt New() =>
        PaymentAttempt.Start(Guid.NewGuid(), "cust-1", "key-1", new Money(270m, new CurrencyCode("XTS")), _now);
}
