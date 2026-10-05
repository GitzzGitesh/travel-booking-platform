using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Integrations.Payments.Stripe;

/// <summary>
/// <c>Integrations:Payments:Stripe</c>. The secret key and the webhook signing secret come from user-secrets or Key
/// Vault only (security rules). The publishable key is the only Stripe key the frontend may ever see: configured here
/// and handed to customer-web for the Payment Element (ADR 0006, P9).
/// </summary>
public sealed class StripeOptions : SupplierHttpOptions
{
    public const string SectionName = "Integrations:Payments:Stripe";

    /// <summary>The account's secret (or restricted) API key. Test mode only until the adapter is production-ready.</summary>
    public string? SecretKey { get; set; }

    /// <summary>A test-mode key (the only kind accepted until the adapter is production-ready).</summary>
    internal bool IsTestMode => SecretKey is not null && (SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) || SecretKey.StartsWith("rk_test_", StringComparison.Ordinal));

    /// <summary>
    /// The account's publishable key, for the Payment Element in customer-web (ADR 0006, P9). Not a secret (it is meant
    /// for browsers), but per account and environment, so it comes from configuration. Test mode only until the adapter
    /// is production-ready.
    /// </summary>
    public string? PublishableKey { get; set; }

    /// <summary>The webhook endpoint's signing secret (<c>whsec_...</c>).</summary>
    public string? WebhookSigningSecret { get; set; }

    /// <summary>The Stripe API version every request pins (the <c>Stripe-Version</c> header), so responses never change shape silently.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>How old a signed notification may be (Stripe's libraries default to 5 minutes).</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The charge currencies this account may use (ISO-4217). Only two-decimal currencies are supported until Stripe
    /// confirms our account's rules for three-decimal ones (TND, ADR 0006). Adding INR or TND needs its confirmation.
    /// </summary>
    public List<string> Currencies { get; set; } = [];

    /// <summary>Two-decimal currencies whose minor units this adapter converts: the Q5 charge-currency candidates.</summary>
    internal static readonly HashSet<string> TwoDecimalCurrencies = ["USD", "EUR", "GBP", "INR"];

    public override IEnumerable<string> Problems()
    {
        foreach (var problem in base.Problems())
        {
            yield return problem;
        }

        // Test keys only: the adapter is not production-ready (ADR 0006), and live keys never reach a non-production host.
        if (!IsTestMode)
        {
            yield return "SecretKey must be a Stripe test-mode key (sk_test_ or rk_test_) until the adapter is production-ready.";
        }

        if (PublishableKey is null || !PublishableKey.StartsWith("pk_test_", StringComparison.Ordinal))
        {
            yield return "PublishableKey must be a Stripe test-mode publishable key (pk_test_) until the adapter is production-ready.";
        }

        if (WebhookSigningSecret is null || !WebhookSigningSecret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            yield return "WebhookSigningSecret (whsec_...) is required.";
        }

        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            yield return "ApiVersion must pin a Stripe API version.";
        }

        if (WebhookTolerance <= TimeSpan.Zero || WebhookTolerance > TimeSpan.FromMinutes(10))
        {
            yield return "WebhookTolerance must be between 0 and 10 minutes (never 0: that disables the replay check).";
        }

        if (Currencies.Count == 0 || Currencies.Any(c => !CurrencyCode.IsValid(c) || !TwoDecimalCurrencies.Contains(c)))
        {
            yield return $"Currencies must list at least one of {string.Join(", ", TwoDecimalCurrencies)}; others need Stripe's confirmation first.";
        }
    }
}

public static class StripeRegistration
{
    /// <summary>Whether configuration enables Stripe; the host then composes it instead of the mock.</summary>
    public static bool IsStripeEnabled(this IConfiguration configuration) =>
        configuration.GetSection(StripeOptions.SectionName).GetValue<bool>(nameof(SupplierHttpOptions.Enabled));

    /// <summary>
    /// Composes Stripe as the payment provider, with its notifications, only when <c>Enabled</c> is true; its settings
    /// are then validated at startup. Not production-ready: startup refuses it outside Development and Staging.
    /// </summary>
    public static IServiceCollection AddStripePaymentProvider(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.IsStripeEnabled())
        {
            return services;
        }

        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .Validate(o => !o.Problems().Any(), $"{StripeOptions.SectionName} is incomplete: set BaseUrl (https), SecretKey and WebhookSigningSecret (test mode, from secrets), PublishableKey (pk_test_), ApiVersion and Currencies.")
            .ValidateOnStart();

        // No resilience pipeline: writes are never retried (booking rules), and lookups are repeated by reconciliation.
        services.AddHttpClient(StripePaymentProvider.HttpClientName, (provider, client) =>
        {
            client.BaseAddress = provider.GetRequiredService<IOptions<StripeOptions>>().Value.BaseUrl;
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPaymentProvider>(provider => new StripePaymentProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<StripeOptions>>(),
            provider.GetRequiredService<ILogger<StripePaymentProvider>>()));
        services.AddSingleton<IPaymentNotifications>(provider => new StripeNotifications(
            provider.GetRequiredService<IOptions<StripeOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
