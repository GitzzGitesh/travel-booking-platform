using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace TravelBooking.BuildingBlocks.Http;

/// <summary>
/// The per-customer limit on a signed-in customer's writes (Q10: orders, travellers, and checkout when exposed). It runs
/// as an endpoint filter, after authorization has established the internal customer id, so one account cannot spread its
/// writes over many addresses. The per-address limits still run first, before authentication, so unauthenticated and
/// bad-token requests are limited too. Reads are not counted here (status polling), only by address.
/// </summary>
public sealed class CustomerWriteLimiter(PartitionedRateLimiter<string> limiter) : IDisposable
{
    internal RateLimitLease Acquire(string customerId) => limiter.AttemptAcquire(customerId);

    public void Dispose() => limiter.Dispose();
}

public static class CustomerWriteLimits
{
    /// <summary>Limits the unsafe methods (not GET or HEAD) of these endpoints per signed-in customer; 429 when exceeded.</summary>
    public static TBuilder RequireCustomerWriteLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || http.User.CustomerId() is not { } customerId)
            {
                return await next(context); // without a customer, authorization has already refused the request
            }

            using var lease = http.RequestServices.GetRequiredService<CustomerWriteLimiter>().Acquire(customerId);
            if (!lease.IsAcquired)
            {
                if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return TypedResults.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited", title: "Too many requests. Please wait a moment and try again.");
            }

            return await next(context);
        });
}
