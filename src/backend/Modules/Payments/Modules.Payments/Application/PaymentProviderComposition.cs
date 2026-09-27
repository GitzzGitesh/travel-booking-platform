using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Application;

/// <summary>Validated at startup; it has no settings of its own.</summary>
internal sealed class PaymentProviderComposition;

/// <summary>
/// Checked at startup, in both hosts (ADR 0006):
/// - at most one payment provider is composed (a payment is always handled by the provider that made it);
/// - outside Development and Staging, the provider is production-ready. A mock, or an adapter not yet verified in its
///   provider's test mode with production access, never takes real payments;
/// - the not-found window is at least the provider's minimum;
/// - every notification verifier belongs to the composed provider.
/// </summary>
internal sealed class PaymentProviderCompositionValidator(
    IEnumerable<IPaymentProvider> providers,
    IEnumerable<IPaymentNotifications> notifications,
    IOptions<PaymentReconciliationOptions> reconciliation,
    IHostEnvironment environment)
    : IValidateOptions<PaymentProviderComposition>
{
    public ValidateOptionsResult Validate(string? name, PaymentProviderComposition options)
    {
        var composed = providers.ToList();
        var failures = new List<string>();
        if (composed.Count > 1)
        {
            failures.Add($"Several payment providers are composed ({string.Join(", ", composed.Select(p => p.Id))}); exactly one is supported.");
        }

        if (!environment.IsDevelopment() && !environment.IsStaging())
        {
            failures.AddRange(composed
                .Where(p => !p.IsProductionReady)
                .Select(p => $"Payment provider '{p.Id}' is not production-ready, and only production-ready providers run in {environment.EnvironmentName}."));
        }

        // A "not found" concluded before the provider's lookup has caught up could let a second hold start (ADR 0006).
        failures.AddRange(composed
            .Where(p => reconciliation.Value.NotFoundConclusiveAfter < p.MinimumNotFoundWindow)
            .Select(p => $"{PaymentReconciliationOptions.SectionName}:NotFoundConclusiveAfter must be at least {p.MinimumNotFoundWindow} for payment provider '{p.Id}'."));

        failures.AddRange(notifications
            .Where(n => composed.All(p => p.Id != n.ProviderId))
            .Select(n => $"Payment notifications for '{n.ProviderId}' are composed without that payment provider."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
