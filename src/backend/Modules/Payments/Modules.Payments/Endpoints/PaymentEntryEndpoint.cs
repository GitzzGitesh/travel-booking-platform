using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.Endpoints;

/// <summary>
/// How customer-web collects a payment method (ADR 0006): as the configured provider declares it, or unavailable when
/// there is none. Only provider tokens, labels and a publishable key: never card data, secret keys or provider ids.
/// </summary>
internal static class PaymentEntryEndpoint
{
    public static Ok<PaymentEntryResponse> Handle(IServiceProvider services)
    {
        var entry = services.GetService<IPaymentProvider>()?.Entry ?? PaymentEntry.Unavailable;
        return TypedResults.Ok(new PaymentEntryResponse(entry.Mode.ToString(),
            [.. entry.TestMethods.Select(m => new TestPaymentMethodResponse(m.Token, m.Label))],
            entry.Mode is PaymentEntryMode.Card ? entry.PublishableKey : null));
    }
}

/// <param name="Mode">"Card" (the provider's card component), "Test" (pick a test method) or "Unavailable" (checkout is not offered yet).</param>
/// <param name="PublishableKey">For "Card": the provider's publishable browser key (never a secret).</param>
internal sealed record PaymentEntryResponse(string Mode, IReadOnlyList<TestPaymentMethodResponse> TestMethods, string? PublishableKey);

/// <param name="Token">The payment method token to send to checkout.</param>
internal sealed record TestPaymentMethodResponse(string Token, string Label);
