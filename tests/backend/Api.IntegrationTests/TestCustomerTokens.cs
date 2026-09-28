using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Customer tokens for tests, signed with a key generated here (never a real tenant's). The Api keeps its real
/// JwtBearer validation (signature, issuer, audience, lifetime, RS256 only); tests only point it at this key, issuer and
/// audience instead of an Entra External ID tenant's metadata, which does not exist yet (ADR 0008). JwtBearer checks
/// lifetimes against the system clock, so tokens are issued at real time, whatever the tests' fake clock says.
/// </summary>
internal static class TestCustomerTokens
{
    public const string Issuer = "https://customers.test.invalid/tenant/v2.0";
    public const string Audience = "api://travel-booking-tests";

    private static readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>Trust only the test key, issuer and audience; nothing is fetched from an authority.</summary>
    public static IServiceCollection UseTestCustomerTokens(this IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(CustomerIdentity.Scheme, options =>
        {
            options.Authority = null;
            options.Audience = null;
            options.MetadataAddress = null!;
            options.ConfigurationManager = null;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.ValidAudience = Audience;
            options.TokenValidationParameters.IssuerSigningKey = _key;
        });

    /// <summary>A valid token for this account (<c>oid</c>), unless a parameter says otherwise.</summary>
    /// <param name="objectId">The user's object id; null for a token without one.</param>
    public static string For(
        string? objectId,
        DateTimeOffset now,
        string issuer = Issuer,
        string audience = Audience,
        TimeSpan? lifetime = null,
        bool wrongKey = false,
        string algorithm = SecurityAlgorithms.RsaSha256,
        IEnumerable<Claim>? extraClaims = null)
    {
        List<Claim> claims = [new("sub", $"pairwise-{Guid.NewGuid():N}")]; // per application in Entra: never our key
        if (objectId is not null)
        {
            claims.Add(new("oid", objectId));
        }

        claims.AddRange(extraClaims ?? []);
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(30));
        var issuedAt = (expires < now ? expires : now).AddMinutes(-5);
        var credentials = algorithm == SecurityAlgorithms.HmacSha256
            ? new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), algorithm)
            : new SigningCredentials(wrongKey ? _otherKey : _key, algorithm);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = issuedAt.UtcDateTime,
            IssuedAt = issuedAt.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = credentials,
        });
    }
}
