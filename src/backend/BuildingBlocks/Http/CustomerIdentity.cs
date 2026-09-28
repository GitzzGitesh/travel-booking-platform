using System.Security.Claims;

namespace TravelBooking.BuildingBlocks.Http;

/// <summary>
/// The authenticated customer, as every module sees it (ADR 0008). The Customers module validates the identity
/// provider's token and adds our internal customer id in a separate identity (<see cref="MappedIdentityType"/>), which
/// only it creates; endpoints require <see cref="Policy"/> and read the id with <see cref="CustomerId"/>. Modules never
/// read the identity provider's own claims, so a token cannot choose its customer id.
/// </summary>
public static class CustomerIdentity
{
    /// <summary>The authentication scheme for customer tokens (Entra External ID, ADR 0008). Staff get their own scheme.</summary>
    public const string Scheme = "Customer";

    /// <summary>The authorization policy for customer endpoints: a validated customer token mapped to an internal customer.</summary>
    public const string Policy = "customer";

    /// <summary>Our internal customer id (not the identity provider's subject). Reserved: a token carrying it is refused.</summary>
    public const string CustomerIdClaim = "tb_customer_id";

    /// <summary>The authentication type of the identity holding <see cref="CustomerIdClaim"/>, added by the Customers module only.</summary>
    public const string MappedIdentityType = "tb-customer";

    /// <summary>The internal customer id; null for anyone who is not an authenticated, mapped customer.</summary>
    public static string? CustomerId(this ClaimsPrincipal user)
    {
        var mapped = user.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == MappedIdentityType).ToList();
        return mapped.Count == 1 && user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == Scheme)
            ? mapped[0].Claims.SingleOrDefault(c => c.Type == CustomerIdClaim)?.Value
            : null;
    }
}
