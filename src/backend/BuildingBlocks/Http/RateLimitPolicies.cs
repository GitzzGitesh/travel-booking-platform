namespace TravelBooking.BuildingBlocks.Http;

/// <summary>
/// Rate-limit policy names shared by the host, which defines the limits, and the modules, which tag their endpoints
/// (security rules: rate-limit search, auth-adjacent and booking endpoints). Every anonymous API endpoint carries one.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Anonymous API calls: the default for the <c>/api/v1</c> group.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Anonymous calls that reach a paid supplier (search, revalidation): a tighter limit against scraping.</summary>
    public const string SupplierCalls = "supplier-calls";
}
