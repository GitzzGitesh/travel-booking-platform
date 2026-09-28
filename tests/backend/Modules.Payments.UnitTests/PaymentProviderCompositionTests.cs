using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Payments.Application;
using TravelBooking.Modules.Payments.Ports;

namespace TravelBooking.Modules.Payments.UnitTests;

/// <summary>Startup composition rules for the payment provider (ADR 0006): one provider, production-ready outside Development and Staging.</summary>
public sealed class PaymentProviderCompositionTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    public void A_provider_that_is_not_production_ready_runs_only_in_development_and_staging(string environment, bool allowed) =>
        Validate(environment, [new ScriptedProvider()]).Succeeded.ShouldBe(allowed);

    [Fact]
    public void A_production_ready_provider_runs_in_production() =>
        Validate("Production", [new ReadyProvider()]).Succeeded.ShouldBeTrue();

    [Fact]
    public void Two_providers_are_refused() =>
        Validate("Development", [new ScriptedProvider(), new ReadyProvider()]).Succeeded.ShouldBeFalse();

    [Fact]
    public void Notifications_without_their_provider_are_refused() =>
        Validate("Development", [new ScriptedProvider()], [new OtherNotifications()]).Succeeded.ShouldBeFalse();

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void The_not_found_window_is_at_least_the_provider_minimum(int minutes, bool allowed) =>
        Validate("Development", [new ReadyProvider()], window: TimeSpan.FromMinutes(minutes)).Succeeded.ShouldBe(allowed);

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(
        string environment, IPaymentProvider[] providers, IPaymentNotifications[]? notifications = null, TimeSpan? window = null) =>
        new PaymentProviderCompositionValidator(
            providers,
            notifications ?? [],
            Microsoft.Extensions.Options.Options.Create(new PaymentReconciliationOptions { NotFoundConclusiveAfter = window ?? TimeSpan.FromHours(1) }),
            new Environment(environment)).Validate(null, new PaymentProviderComposition());

    private sealed class ReadyProvider : IPaymentProvider
    {
        private readonly ScriptedProvider _inner = new();

        public string Id => "ready";

        public bool IsProductionReady => true;

        public TimeSpan MinimumNotFoundWindow => TimeSpan.FromHours(1);

        public Task<Result<PaymentSnapshot, BuildingBlocks.Providers.ProviderError>> AuthorizeAsync(AuthorizationDetails details, CancellationToken cancellationToken) => _inner.AuthorizeAsync(details, cancellationToken);

        public Task<Result<PaymentSnapshot, BuildingBlocks.Providers.ProviderError>> CaptureAsync(CaptureDetails details, CancellationToken cancellationToken) => _inner.CaptureAsync(details, cancellationToken);

        public Task<Result<PaymentSnapshot, BuildingBlocks.Providers.ProviderError>> VoidAsync(VoidDetails details, CancellationToken cancellationToken) => _inner.VoidAsync(details, cancellationToken);

        public Task<Result<PaymentRefund, BuildingBlocks.Providers.ProviderError>> RefundAsync(RefundDetails details, CancellationToken cancellationToken) => _inner.RefundAsync(details, cancellationToken);

        public Task<Result<PaymentLookup, BuildingBlocks.Providers.ProviderError>> RetrieveAsync(PaymentReference reference, CancellationToken cancellationToken) => _inner.RetrieveAsync(reference, cancellationToken);

        public Task<Result<RefundLookup, BuildingBlocks.Providers.ProviderError>> RetrieveRefundAsync(PaymentReference reference, OperationKey key, CancellationToken cancellationToken) => _inner.RetrieveRefundAsync(reference, key, cancellationToken);
    }

    private sealed class OtherNotifications : IPaymentNotifications
    {
        public string ProviderId => "someone-else";

        public Result<PaymentNotification, PaymentNotificationRejection> Verify(string body, IReadOnlyDictionary<string, string> headers) =>
            Result<PaymentNotification, PaymentNotificationRejection>.Failure(PaymentNotificationRejection.InvalidSignature);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
