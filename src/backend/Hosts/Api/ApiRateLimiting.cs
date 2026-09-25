using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Api;

/// <summary>A fixed-window limit per client.</summary>
public sealed class RateLimitWindowOptions
{
    [Range(1, 100_000)]
    public int PermitLimit { get; set; }

    [Range(1, 3600)]
    public int WindowSeconds { get; set; }
}

/// <summary>
/// Per-client limits for the named policies (<see cref="RateLimitPolicies"/>). Defaults live in appsettings.json;
/// each deployment may tune them. Limits are per Api instance until a distributed limiter exists (ADR 0011).
/// </summary>
public sealed class ApiRateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public RateLimitWindowOptions Anonymous { get; set; } = new();

    public RateLimitWindowOptions SupplierCalls { get; set; } = new();
}

internal static class ApiRateLimiting
{
    /// <summary>
    /// Partitions by the connection's client address, which is the real client only when the forwarded-headers
    /// middleware has been told which proxies to trust (<see cref="ApiForwardedHeaders"/>). Forwarding headers are
    /// never read here: an untrusted X-Forwarded-For would let any client pick its own bucket. A connection without an
    /// address shares one bucket (fails closed, never unlimited).
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiRateLimitingOptions>()
            .Bind(configuration.GetSection(ApiRateLimitingOptions.SectionName))
            .Validate(o => IsValid(o.Anonymous) && IsValid(o.SupplierCalls), "RateLimiting windows need PermitLimit 1-100000 and WindowSeconds 1-3600.")
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = WriteRejection;
            AddPolicy(limiter, RateLimitPolicies.Anonymous, options => options.Anonymous);
            AddPolicy(limiter, RateLimitPolicies.SupplierCalls, options => options.SupplierCalls);
        });
        return services;
    }

    private static bool IsValid(RateLimitWindowOptions window) =>
        Validator.TryValidateObject(window, new ValidationContext(window), null, validateAllProperties: true);

    internal static string ClientPartition(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address)
        {
            return "unknown-client";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // One IPv6 client normally controls a whole /64 (2^64 addresses): key by that prefix, or a client could rotate
        // addresses to get a fresh bucket for every request.
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static void AddPolicy(RateLimiterOptions limiter, string name, Func<ApiRateLimitingOptions, RateLimitWindowOptions> select) =>
        limiter.AddPolicy(name, context =>
        {
            var window = select(context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiRateLimitingOptions>>().Value);
            return RateLimitPartition.GetFixedWindowLimiter(ClientPartition(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = window.PermitLimit,
                Window = TimeSpan.FromSeconds(window.WindowSeconds),
                QueueLimit = 0,
            });
        });

    // 429 as Problem Details (api-design rules), with Retry-After when the limiter knows it.
    private static async ValueTask WriteRejection(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var http = rejected.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Type = "rate-limited",
                Title = "Too many requests. Please wait a moment and try again.",
            },
        });
    }
}

/// <summary>
/// Which reverse proxies may set the client address and scheme (X-Forwarded-For / X-Forwarded-Proto). Empty by
/// default: NO proxy is trusted, not even loopback, until a deployment names its ingress (the hosting decision).
/// </summary>
public sealed class ApiForwardedHeadersOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Exact proxy addresses, such as "10.0.0.4".</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form, such as "10.0.0.0/24".</summary>
    public string[] KnownNetworks { get; set; } = [];
}

internal static class ApiForwardedHeaders
{
    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiForwardedHeadersOptions>()
            .Bind(configuration.GetSection(ApiForwardedHeadersOptions.SectionName))
            .Validate(o => o.KnownProxies.All(p => IPAddress.TryParse(p, out _)), "ForwardedHeaders:KnownProxies must be IP addresses.")
            .Validate(o => o.KnownNetworks.All(n => System.Net.IPNetwork.TryParse(n, out _)), "ForwardedHeaders:KnownNetworks must be CIDR networks.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<ApiForwardedHeadersOptions>>((forwarded, trusted) =>
            {
                // With BOTH lists empty the middleware trusts every sender, so "no proxy configured" must switch
                // forwarding off entirely rather than clear the lists. (Never set ASPNETCORE_FORWARDEDHEADERS_ENABLED:
                // it enables that trust-everyone mode.)
                var anyTrusted = trusted.Value.KnownProxies.Length > 0 || trusted.Value.KnownNetworks.Length > 0;
                forwarded.ForwardedHeaders = anyTrusted
                    ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                    : ForwardedHeaders.None;

                // Only the nearest proxy's entry is used: a client can prepend anything to the header.
                forwarded.ForwardLimit = 1;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var proxy in trusted.Value.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in trusted.Value.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });
        return services;
    }
}
