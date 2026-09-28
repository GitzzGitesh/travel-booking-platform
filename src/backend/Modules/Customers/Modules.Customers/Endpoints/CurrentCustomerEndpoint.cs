using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Modules.Customers.Endpoints;

/// <summary>The signed-in customer, as the Api knows them: lets the frontend confirm a session. No personal data.</summary>
internal static class CurrentCustomerEndpoint
{
    public static Ok<CurrentCustomerResponse> Handle(ClaimsPrincipal user) => TypedResults.Ok(new CurrentCustomerResponse(user.CustomerId()!));
}

/// <param name="CustomerId">Our internal customer id, never the identity provider's subject.</param>
internal sealed record CurrentCustomerResponse(string CustomerId);
