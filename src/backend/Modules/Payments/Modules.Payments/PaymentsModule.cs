using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TravelBooking.Modules.Payments.Application;

namespace TravelBooking.Modules.Payments;

/// <summary>
/// The Payments module's entry point (ADR 0004: it owns the <see cref="Ports.IPaymentProvider"/> port). The host composes
/// a provider adapter. No endpoints and no persistence yet: the Payment record, its webhooks and the checkout
/// orchestration come with later chunks; the real provider waits for ADR 0006 (Q2, Q5).
/// </summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PaymentOperations>();
        return services;
    }
}
