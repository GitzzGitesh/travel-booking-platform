using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// Staff tokens for tests, signed with a key generated here (never a real tenant's), for an issuer and audience distinct
/// from the customer test tokens: the Api keeps its real JwtBearer validation and only trusts this key (ADR 0022).
/// </summary>
internal static class TestStaffTokens
{
    public const string Issuer = "https://staff.test.invalid/tenant/v2.0";
    public const string Audience = "api://travel-booking-staff-tests";

    /// <summary>Object ids granted roles in the test configuration (Access:RoleAssignments).</summary>
    public const string Operations = "staff-operations";
    public const string Unassigned = "staff-without-roles";

    private static readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "staff-test-key" };

    public static IServiceCollection UseTestStaffTokens(this IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(StaffIdentity.Scheme, options =>
        {
            options.Authority = null;
            options.Audience = null;
            options.MetadataAddress = null!;
            options.ConfigurationManager = null;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.ValidAudience = Audience;
            options.TokenValidationParameters.IssuerSigningKey = _key;
        });

    /// <summary>A valid staff token (signed in with MFA) for this account, unless a parameter says otherwise.</summary>
    public static string For(string? objectId, bool mfa = true, IEnumerable<Claim>? extraClaims = null)
    {
        var now = DateTimeOffset.UtcNow; // JwtBearer checks lifetimes against the system clock
        List<Claim> claims = [new("sub", $"pairwise-{Guid.NewGuid():N}"), new("amr", "pwd")];
        if (objectId is not null)
        {
            claims.Add(new Claim("oid", objectId));
        }

        if (mfa)
        {
            claims.Add(new Claim("amr", "mfa"));
        }

        claims.AddRange(extraClaims ?? []);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.AddMinutes(-5).UtcDateTime,
            IssuedAt = now.AddMinutes(-5).UtcDateTime,
            Expires = now.AddMinutes(30).UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }
}
