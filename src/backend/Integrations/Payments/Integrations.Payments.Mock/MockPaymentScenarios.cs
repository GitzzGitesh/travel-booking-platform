using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TravelBooking.Integrations.Payments.Mock.Persistence;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Mock;

/// <summary>
/// Test payment method tokens that choose the mock's outcome, per payment (provider-integration.md: magic values).
/// They are the mock's own tokens: never real card numbers (testing rules).
/// </summary>
public static class MockPaymentMethods
{
    /// <summary>Authorized; capture, void and refund succeed.</summary>
    public const string Approved = "pm_mock_approved";

    /// <summary>Declined (generic): a Declined payment, nothing held (F-20).</summary>
    public const string Declined = "pm_mock_declined";

    /// <summary>Declined for insufficient funds (F-20).</summary>
    public const string InsufficientFunds = "pm_mock_insufficient_funds";

    /// <summary>Needs a customer challenge (SCA) that is never completed: the customer abandoned it (F-21).</summary>
    public const string RequiresAction = "pm_mock_requires_action";

    /// <summary>Needs a customer challenge, which the customer then completes: the next lookup finds it Authorized.</summary>
    public const string ChallengeCompleted = "pm_mock_challenge_completed";

    /// <summary>Needs a customer challenge, which then fails (e.g. 3DS authentication refused): the next lookup finds it Declined.</summary>
    public const string ChallengeFailed = "pm_mock_challenge_failed";

    /// <summary>The authorization times out (Unknown) but WAS made: a lookup by our reference finds it.</summary>
    public const string TimeoutAuthorized = "pm_mock_timeout_authorized";

    /// <summary>The authorization times out (Unknown) and nothing was authorized: a lookup finds nothing.</summary>
    public const string TimeoutNotAuthorized = "pm_mock_timeout_not_authorized";

    /// <summary>Authorized; the first capture times out after capturing, and a retry with the same key returns it (F-23).</summary>
    public const string CaptureTimeout = "pm_mock_capture_timeout";

    /// <summary>Authorized and captured; the first refund is refused unprocessed, and a retry with the same key succeeds (F-43).</summary>
    public const string RefundUnavailableOnce = "pm_mock_refund_unavailable_once";

    /// <summary>Authorized and captured; refunds are accepted as Pending (not yet final), as some providers report them.</summary>
    public const string RefundPending = "pm_mock_refund_pending";

    internal static readonly HashSet<string> All =
        [Approved, Declined, InsufficientFunds, RequiresAction, ChallengeCompleted, ChallengeFailed, TimeoutAuthorized, TimeoutNotAuthorized, CaptureTimeout, RefundUnavailableOnce, RefundPending];
}

/// <summary>Provider-wide scenarios, selected by configuration, never by production data.</summary>
public enum MockPaymentScenario
{
    Success,

    /// <summary>Every call is refused before processing.</summary>
    Unavailable,
}

/// <summary>Where the mock keeps its payments (ADR 0032).</summary>
public enum MockPaymentState
{
    /// <summary>In the mock's own SQL schema (paymentsmock, in the Payments database), shared by every process that uses it.</summary>
    Shared,

    /// <summary>In this process only (unit and provider contract tests).</summary>
    InProcess,
}

public sealed class MockPaymentProviderOptions
{
    public const string SectionName = "Integrations:Payments:Mock";

    public MockPaymentScenario Scenario { get; set; } = MockPaymentScenario.Success;

    /// <summary>Where the mock keeps its payments (ADR 0032). Shared by default, so the Api and the Worker see the same ones.</summary>
    public MockPaymentState State { get; set; } = MockPaymentState.Shared;
}

public static class MockPaymentProviderRegistration
{
    /// <summary>
    /// Registers the mock as the <see cref="IPaymentProvider"/>. Development or Staging only (an allow-list), with the
    /// scenario validated at startup and on first use.
    /// </summary>
    public static IServiceCollection AddMockPaymentProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MockPaymentProviderOptions>()
            .Bind(configuration.GetSection(MockPaymentProviderOptions.SectionName))
            .Validate<IHostEnvironment>((_, environment) => environment.IsDevelopment() || environment.IsStaging(), "The mock payment provider only runs in Development or Staging.")
            .Validate(options => Enum.IsDefined(options.Scenario), $"{MockPaymentProviderOptions.SectionName}:Scenario is not a defined scenario.")
            // In Staging the Api and the Worker run apart: keeping payments in one process would split them again (ADR 0032).
            .Validate<IHostEnvironment>((options, environment) => options.State is not MockPaymentState.InProcess || environment.IsDevelopment(),
                $"{MockPaymentProviderOptions.SectionName}:State InProcess is for Development only (tests); Staging shares the mock's payments.")
            .ValidateOnStart();
        services.AddSingleton<IPaymentProvider, MockPaymentProvider>();

        // The ledger is chosen when the services are composed, so it is read here; an unknown value fails at once.
        var state = configuration.GetSection(MockPaymentProviderOptions.SectionName).GetValue(nameof(MockPaymentProviderOptions.State), MockPaymentState.Shared);
        switch (state)
        {
            case MockPaymentState.Shared:
                services.AddSqlMockPaymentLedger(configuration);
                break;
            case MockPaymentState.InProcess:
                services.AddSingleton<IMockPaymentLedger, InProcessMockPaymentLedger>();
                break;
            default:
                throw new InvalidOperationException($"{MockPaymentProviderOptions.SectionName}:State is not a defined state.");
        }

        return services;
    }
}
