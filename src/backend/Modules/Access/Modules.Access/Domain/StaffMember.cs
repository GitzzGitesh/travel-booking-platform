namespace TravelBooking.Modules.Access.Domain;

/// <summary>
/// A staff member, identified by a workforce identity-provider account (ADR 0008): the token's issuer and the user's
/// object id (Entra <c>oid</c>) map to one internal <see cref="Id"/>, which is the only staff id timelines, audit entries
/// and logs ever carry. No name or email is kept.
/// </summary>
internal sealed class StaffMember
{
    public const int MaxIssuerLength = 256;
    public const int MaxObjectIdLength = 128;

    private StaffMember()
    {
    }

    public Guid Id { get; private set; }

    public string IdentityIssuer { get; private set; } = string.Empty;

    public string IdentityObjectId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The id other modules see (timelines, audit): the internal id without dashes.</summary>
    public string StaffId => Id.ToString("N");

    // Printable ASCII without spaces, compared exactly: two accounts never collapse into one staff member.
    public static bool IsValidIdentity(string? issuer, string? objectId) =>
        issuer is { Length: > 0 and <= MaxIssuerLength } && objectId is { Length: > 0 and <= MaxObjectIdLength }
        && issuer.All(c => c is > ' ' and <= '~') && objectId.All(c => c is > ' ' and <= '~');

    public static StaffMember For(string issuer, string objectId, DateTimeOffset at) =>
        IsValidIdentity(issuer, objectId)
            ? new StaffMember { Id = Guid.NewGuid(), IdentityIssuer = issuer, IdentityObjectId = objectId, CreatedAt = at }
            : throw new ArgumentException("An identity issuer and object id are required.");
}
