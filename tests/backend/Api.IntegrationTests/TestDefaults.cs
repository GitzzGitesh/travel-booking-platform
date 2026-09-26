using System.Runtime.CompilerServices;

// WebApplicationFactory intercepts Program's host build through a process-wide listener, so factories built in parallel
// can capture each other's host and configuration (flaky startup-validation tests in CI). Run the Api tests one at a time.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

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
