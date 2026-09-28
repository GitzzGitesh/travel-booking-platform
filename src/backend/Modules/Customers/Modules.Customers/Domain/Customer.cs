namespace TravelBooking.Modules.Customers.Domain;

/// <summary>
/// Our customer, identified by an identity-provider account (ADR 0008): the token's issuer and the user's object id
/// (Entra <c>oid</c>, stable across the tenant's applications, unlike the per-application <c>sub</c>) map to one
/// internal <see cref="Id"/>, which is the only customer id other modules, the timeline and logs ever see. The object id
/// is pseudonymous personal data (security.md). No name, email, phone or traveller data is kept here until retention (Q9)
/// is decided.
/// </summary>
internal sealed class Customer
{
    public const int MaxIssuerLength = 256;
    public const int MaxSubjectLength = 128;

    private Customer()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The token issuer (the identity provider's tenant): subjects are unique only within one.</summary>
    public string IdentityIssuer { get; private set; } = string.Empty;

    public string IdentitySubject { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The id other modules use: the internal id without dashes.</summary>
    public string CustomerId => Id.ToString("N");

    // Printable ASCII without spaces, compared exactly (a binary collation): two accounts never collapse into one customer.
    public static bool IsValidIdentity(string? issuer, string? subject) =>
        issuer is { Length: > 0 and <= MaxIssuerLength } && subject is { Length: > 0 and <= MaxSubjectLength }
        && issuer.All(c => c is > ' ' and <= '~') && subject.All(c => c is > ' ' and <= '~');

    public static Customer For(string issuer, string subject, DateTimeOffset at) =>
        IsValidIdentity(issuer, subject)
            ? new Customer { Id = Guid.NewGuid(), IdentityIssuer = issuer, IdentitySubject = subject, CreatedAt = at }
            : throw new ArgumentException("An identity issuer and subject are required.");
}
