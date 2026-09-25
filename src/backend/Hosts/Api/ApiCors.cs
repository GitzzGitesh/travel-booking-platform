using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace TravelBooking.Api;

/// <summary>
/// Explicit, per-environment CORS allow-list (.claude/rules/security.md): configured in <c>Cors:AllowedOrigins</c>,
/// empty by default (browsers then block cross-origin calls). Wildcards and credentials are never allowed; the
/// credentialed-cookie decision belongs to the identity story (ADR 0008).
/// </summary>
internal static class ApiCors
{
    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .Validate<IHostEnvironment>(
                (settings, environment) => settings.AllowedOrigins.All(origin => IsExactOrigin(origin, allowLoopbackHttp: environment.IsDevelopment())),
                "Cors:AllowedOrigins must contain exact, canonical origins (https://host[:port]; http://localhost only in Development), with no wildcards or credentials.")
            .ValidateOnStart();

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<CorsSettings>>((cors, settings) => cors.AddDefaultPolicy(policy => policy
                .WithOrigins(settings.Value.AllowedOrigins)
                .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete)
                .WithHeaders("Content-Type", "Authorization", "Idempotency-Key", "If-Match")
                .WithExposedHeaders("ETag", "Location", "Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    // Exactly scheme://host[:port] in canonical form, which rules out paths, queries, fragments, trailing slashes,
    // wildcards, explicit default ports, whitespace, and upper case. No user info (https://good@evil) and ASCII or
    // punycode hosts only, because CORS normalises IDN hosts and would otherwise drop the user info.
    internal static bool IsExactOrigin(string origin, bool allowLoopbackHttp) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host == uri.IdnHost
        && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);

    internal sealed class CorsSettings
    {
        public const string SectionName = "Cors";

        public string[] AllowedOrigins { get; init; } = [];
    }
}
