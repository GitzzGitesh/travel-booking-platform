using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Payments.Ports;
using TravelBooking.ProviderContracts.Payments;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The shared payment contract against the mock as the hosts compose it, with its payments kept in SQL (ADR 0032): every
/// round trip through the paymentsmock tables (amounts, states, decline reasons, challenge tokens, refunds) keeps the
/// provider's semantics. The same contract runs on the in-process ledger in ProviderContracts.
/// </summary>
public sealed class SharedMockPaymentProviderContractTests(SqlApiFactory api) : PaymentProviderContract, IClassFixture<SqlApiFactory>, IDisposable
{
    private readonly ServiceProvider _services = Compose(api.ConnectionString);

    protected override IPaymentProvider Provider => _services.GetRequiredService<IPaymentProvider>();

    protected override PaymentMethodToken ApprovedMethod => new(MockPaymentMethods.Approved);

    protected override PaymentMethodToken DeclinedMethod => new(MockPaymentMethods.Declined);

    protected override PaymentMethodToken ChallengeMethod => new(MockPaymentMethods.RequiresAction);

    public void Dispose() => _services.Dispose();

    private static ServiceProvider Compose(string connectionString) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Development })
            .AddMockPaymentProvider(new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:Payments", connectionString)])
                .Build())
            .BuildServiceProvider();
}
