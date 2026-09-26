using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>Needs a customer challenge (SCA) that is never completed in the mock (F-21).</summary>
    public const string RequiresAction = "pm_mock_requires_action";

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
        [Approved, Declined, InsufficientFunds, RequiresAction, TimeoutAuthorized, TimeoutNotAuthorized, CaptureTimeout, RefundUnavailableOnce, RefundPending];
}

/// <summary>Provider-wide scenarios, selected by configuration, never by production data.</summary>
public enum MockPaymentScenario
{
    Success,

    /// <summary>Every call is refused before processing.</summary>
    Unavailable,
}

public sealed class MockPaymentProviderOptions
{
    public const string SectionName = "Integrations:Payments:Mock";

    public MockPaymentScenario Scenario { get; set; } = MockPaymentScenario.Success;
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
            .ValidateOnStart();
        services.AddSingleton<IPaymentProvider, MockPaymentProvider>();
        return services;
    }
}
