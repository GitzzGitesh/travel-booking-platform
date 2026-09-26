using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.ProviderContracts.Payments;

/// <summary>The mock passes the shared payment contract, plus its scenarios and guards.</summary>
public sealed class MockPaymentProviderTests : PaymentProviderContract
{
    protected override IPaymentProvider Provider { get; } = Create(nameof(MockPaymentScenario.Success));

    protected override PaymentMethodToken ApprovedMethod => new(MockPaymentMethods.Approved);

    protected override PaymentMethodToken DeclinedMethod => new(MockPaymentMethods.Declined);

    protected override PaymentMethodToken ChallengeMethod => new(MockPaymentMethods.RequiresAction);

    [Fact]
    public async Task Insufficient_funds_is_a_decline_with_its_reason_and_the_reference_stays_declined()
    {
        var details = Authorization(270m, new PaymentMethodToken(MockPaymentMethods.InsufficientFunds));

        var declined = (await Provider.AuthorizeAsync(details, Ct)).Value;
        var replay = (await Provider.AuthorizeAsync(details, Ct)).Value;

        declined.State.ShouldBe(PaymentState.Declined);
        declined.DeclineReason.ShouldBe(PaymentDeclineReason.InsufficientFunds);
        replay.State.ShouldBe(PaymentState.Declined);
        (await Provider.AuthorizeAsync(details with { PaymentMethod = ApprovedMethod }, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.IdempotencyConflict);
    }

    [Fact]
    public async Task An_authorization_timeout_that_authorized_is_found_by_our_reference()
    {
        var details = Authorization(270m, new PaymentMethodToken(MockPaymentMethods.TimeoutAuthorized));

        (await Provider.AuthorizeAsync(details, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);
        (await Provider.RetrieveAsync(details.Reference, Ct)).Value.Payment.ShouldNotBeNull().State.ShouldBe(PaymentState.Authorized);
    }

    [Fact]
    public async Task An_authorization_timeout_that_did_not_authorize_is_not_found()
    {
        var details = Authorization(270m, new PaymentMethodToken(MockPaymentMethods.TimeoutNotAuthorized));

        (await Provider.AuthorizeAsync(details, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);
        (await Provider.RetrieveAsync(details.Reference, Ct)).Value.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task F23_a_capture_that_timed_out_after_capturing_is_returned_by_a_retry_with_the_same_key()
    {
        var payment = await Authorized(270m, new PaymentMethodToken(MockPaymentMethods.CaptureTimeout));
        var capture = Capture(payment, payment.Amount);

        (await Provider.CaptureAsync(capture, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unknown);
        (await Provider.CaptureAsync(capture, Ct)).Value.Captured.ShouldBe(payment.Amount); // captured once, not twice
    }

    [Fact]
    public async Task F43_a_refund_refused_unprocessed_succeeds_when_retried_with_the_same_key()
    {
        var payment = await Captured(270m, new PaymentMethodToken(MockPaymentMethods.RefundUnavailableOnce));
        var refund = new RefundDetails(payment.Reference, payment.Payment, NewKey("refund"), Amount(50m));

        (await Provider.RefundAsync(refund, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
        (await Provider.RetrieveRefundAsync(payment.Reference, refund.Key, Ct)).Value.Found.ShouldBeFalse();
        (await Provider.RefundAsync(refund, Ct)).Value.Status.ShouldBe(RefundStatus.Succeeded);
    }

    [Fact]
    public async Task A_pending_refund_is_reported_as_pending_and_found_by_our_key()
    {
        var payment = await Captured(270m, new PaymentMethodToken(MockPaymentMethods.RefundPending));
        var refund = new RefundDetails(payment.Reference, payment.Payment, NewKey("refund"), Amount(50m));

        (await Provider.RefundAsync(refund, Ct)).Value.Status.ShouldBe(RefundStatus.Pending);
        (await Provider.RetrieveRefundAsync(payment.Reference, refund.Key, Ct)).Value.Refund!.Status.ShouldBe(RefundStatus.Pending);
    }

    [Fact]
    public async Task An_unrecognised_payment_method_is_an_invalid_request()
    {
        var result = await Provider.AuthorizeAsync(Authorization(270m, new PaymentMethodToken("pm_other")), Ct);

        result.Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task The_unavailable_scenario_refuses_every_call()
    {
        var unavailable = Create(nameof(MockPaymentScenario.Unavailable));
        var details = Authorization(270m);

        (await unavailable.AuthorizeAsync(details, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
        (await unavailable.RetrieveAsync(details.Reference, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Prod")]
    public void The_mock_runs_only_in_development_or_staging(string environment) =>
        Should.Throw<OptionsValidationException>(() => Create(nameof(MockPaymentScenario.Success), environment))
            .Message.ShouldContain("only runs in Development or Staging");

    private static IPaymentProvider Create(string scenario, string environment = "Development")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new($"{MockPaymentProviderOptions.SectionName}:Scenario", scenario)])
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
            .AddMockPaymentProvider(configuration)
            .BuildServiceProvider();
        _ = services.GetRequiredService<IOptions<MockPaymentProviderOptions>>().Value;
        return services.GetRequiredService<IPaymentProvider>();
    }
}
