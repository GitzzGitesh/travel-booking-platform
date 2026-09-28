using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace TravelBooking.BuildingBlocks.Http;

/// <summary>The caller of an endpoint open to anonymous callers: anonymous, a signed-in customer, or a refused token.</summary>
/// <param name="CustomerId">The internal customer id when signed in; null when anonymous or rejected.</param>
/// <param name="IsRejected">A token was sent but is not a valid customer token: answer 401, never treat as anonymous.</param>
public sealed record OptionalCustomer(string? CustomerId, bool IsRejected)
{
    public static OptionalCustomer Anonymous { get; } = new(null, false);

    public static OptionalCustomer Rejected { get; } = new(null, true);
}

/// <summary>
/// The authenticated customer, as every module sees it (ADR 0008). The Customers module validates the identity
/// provider's token and adds our internal customer id in a separate identity (<see cref="MappedIdentityType"/>), which
/// only it creates; endpoints require <see cref="Policy"/> and read the id with <see cref="CustomerId"/>. Modules never
/// read the identity provider's own claims, so a token cannot choose its customer id.
/// </summary>
public static partial class CustomerIdentity
{
    /// <summary>The authentication scheme for customer tokens (Entra External ID, ADR 0008). Staff get their own scheme.</summary>
    public const string Scheme = "Customer";

    /// <summary>The authorization policy for customer endpoints: a validated customer token mapped to an internal customer.</summary>
    public const string Policy = "customer";

    /// <summary>Our internal customer id (not the identity provider's subject). Reserved: a token carrying it is refused.</summary>
    public const string CustomerIdClaim = "tb_customer_id";

    /// <summary>The authentication type of the identity holding <see cref="CustomerIdClaim"/>, added by the Customers module only.</summary>
    public const string MappedIdentityType = "tb-customer";

    /// <summary>
    /// For endpoints open to anonymous callers that act for a signed-in customer when there is one (e.g. selecting a flight):
    /// no <c>Authorization</c> header is anonymous; a header must be a valid customer token that satisfies
    /// <see cref="Policy"/>, otherwise the caller is <see cref="OptionalCustomer.Rejected"/> (never silently anonymous).
    /// </summary>
    public static async Task<OptionalCustomer> AuthenticateOptionalCustomerAsync(this HttpContext http)
    {
        if (!http.Request.Headers.ContainsKey(HeaderNames.Authorization))
        {
            return OptionalCustomer.Anonymous;
        }

        var result = await http.AuthenticateAsync(Scheme);
        if (result.Succeeded
            && (await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(result.Principal, Policy)).Succeeded
            && result.Principal.CustomerId() is { } customerId)
        {
            return new OptionalCustomer(customerId, IsRejected: false);
        }

        // A security event (security rules: authentication failures), with the route and trace only, never the token;
        // and the RFC 6750 challenge on the 401 the endpoint answers.
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CustomerIdentity).FullName!);
        LogRejected(logger, http.Request.Path.Value ?? string.Empty, http.TraceIdentifier);
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return OptionalCustomer.Rejected;
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "CustomerTokenRejected", Message = "Security: a customer token was refused on {Path} (trace {TraceId})")]
    private static partial void LogRejected(ILogger logger, string path, string traceId);

    /// <summary>The internal customer id; null for anyone who is not an authenticated, mapped customer.</summary>
    public static string? CustomerId(this ClaimsPrincipal user)
    {
        var mapped = user.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == MappedIdentityType).ToList();
        return mapped.Count == 1 && user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == Scheme)
            ? mapped[0].Claims.SingleOrDefault(c => c.Type == CustomerIdClaim)?.Value
            : null;
    }
}
