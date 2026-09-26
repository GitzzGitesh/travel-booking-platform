using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The payment port as composed by the Api (Phase 3 chunk 2): the provider-neutral operations over the mock provider.
/// There is no payment endpoint and no persisted payment yet; the checkout orchestration drives these later.
/// </summary>
public sealed class PaymentOperationsHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Money _total = new(540m, new CurrencyCode("XTS"));

    [Fact]
    public async Task Authorize_capture_and_refund_through_the_composed_host()
    {
        using var scope = Development().Services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentOperations>();
        var reference = NewReference();

        var authorized = (await payments.AuthorizeAsync(new AuthorizationDetails(reference, _total, new PaymentMethodToken(MockPaymentMethods.Approved)), Ct))
            .ShouldBeOfType<PaymentOutcome.Authorized>().Payment;
        var captured = await payments.CaptureAsync(new CaptureDetails(reference, authorized.Payment, new OperationKey($"{reference}:capture"), _total), Ct);
        var refunded = await payments.RefundAsync(new RefundDetails(reference, authorized.Payment, new OperationKey($"{Guid.NewGuid():N}:refund"), _total with { Amount = 40m }), Ct);

        captured.ShouldBeOfType<PaymentOutcome.Captured>();
        refunded.ShouldBeOfType<PaymentOutcome.RefundSucceeded>().Refund.Amount.ShouldBe(_total with { Amount = 40m });
        (await payments.ReconcileAsync(reference, Ct)).ShouldBeOfType<PaymentOutcome.Captured>().Payment.Refunded.ShouldBe(_total with { Amount = 40m });
    }

    [Fact]
    public async Task F22_a_hold_is_voided_when_the_booking_fails()
    {
        using var scope = Development().Services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentOperations>();
        var reference = NewReference();
        var authorized = (await payments.AuthorizeAsync(new AuthorizationDetails(reference, _total, new PaymentMethodToken(MockPaymentMethods.Approved)), Ct))
            .ShouldBeOfType<PaymentOutcome.Authorized>().Payment;

        var voided = await payments.VoidAsync(new VoidDetails(reference, authorized.Payment, new OperationKey($"{reference}:void")), Ct);

        voided.ShouldBeOfType<PaymentOutcome.Voided>();
    }

    [Fact]
    public async Task An_unknown_authorization_is_resolved_by_our_reference_not_re_authorized()
    {
        using var scope = Development().Services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentOperations>();
        var held = NewReference();
        var notHeld = NewReference();

        (await payments.AuthorizeAsync(new AuthorizationDetails(held, _total, new PaymentMethodToken(MockPaymentMethods.TimeoutAuthorized)), Ct))
            .ShouldBeOfType<PaymentOutcome.Unknown>();
        (await payments.AuthorizeAsync(new AuthorizationDetails(notHeld, _total, new PaymentMethodToken(MockPaymentMethods.TimeoutNotAuthorized)), Ct))
            .ShouldBeOfType<PaymentOutcome.Unknown>();

        (await payments.ReconcileAsync(held, Ct)).ShouldBeOfType<PaymentOutcome.Authorized>();
        (await payments.ReconcileAsync(notHeld, Ct)).ShouldBeOfType<PaymentOutcome.NotFound>();
    }

    [Fact]
    public async Task F20_a_decline_holds_nothing()
    {
        using var scope = Development().Services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentOperations>();

        var outcome = await payments.AuthorizeAsync(new AuthorizationDetails(NewReference(), _total, new PaymentMethodToken(MockPaymentMethods.Declined)), Ct);

        outcome.ShouldBe(new PaymentOutcome.Declined(PaymentDeclineReason.Generic));
    }

    [Fact]
    public void Production_has_no_payment_provider()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        production.Services.GetService<IPaymentProvider>().ShouldBeNull();
    }

    private WebApplicationFactory<Program> Development() => factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));

    private static PaymentReference NewReference() => new($"pay-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
