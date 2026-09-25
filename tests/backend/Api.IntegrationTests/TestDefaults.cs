using System.Runtime.CompilerServices;

namespace TravelBooking.Api.IntegrationTests;

internal static class TestDefaults
{
    /// <summary>
    /// TestServer connections have no client address, so every request shares one rate-limit bucket. Functional tests
    /// get generous limits; <see cref="RateLimitingTests"/> overrides them to test the limiter itself.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseRateLimitsForFunctionalTests()
    {
        Environment.SetEnvironmentVariable("RateLimiting__Anonymous__PermitLimit", "100000");
        Environment.SetEnvironmentVariable("RateLimiting__SupplierCalls__PermitLimit", "100000");
    }
}
