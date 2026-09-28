using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Application;

internal interface ICustomerStore
{
    Task<Customer?> FindByIdentityAsync(string issuer, string subject, CancellationToken cancellationToken);

    /// <summary>Adds the customer; false if this identity is already mapped (a unique constraint: a concurrent first sign-in).</summary>
    Task<bool> TryAddAsync(Customer customer, CancellationToken cancellationToken);
}

/// <summary>
/// Maps an identity-provider account to our internal customer id, creating the customer on first use. Idempotent and
/// safe under concurrency: the database allows one customer per issuer and subject, so two first requests at once
/// resolve to the same customer.
/// </summary>
internal sealed class CustomerDirectory(ICustomerStore store, TimeProvider timeProvider)
{
    /// <returns>The internal customer id, or null when the identity is not usable.</returns>
    public async Task<string?> ResolveAsync(string? issuer, string? subject, CancellationToken cancellationToken)
    {
        if (!Customer.IsValidIdentity(issuer, subject))
        {
            return null;
        }

        if (await store.FindByIdentityAsync(issuer!, subject!, cancellationToken) is { } existing)
        {
            return existing.CustomerId;
        }

        var customer = Customer.For(issuer!, subject!, timeProvider.GetUtcNow());
        if (await store.TryAddAsync(customer, cancellationToken))
        {
            return customer.CustomerId;
        }

        return (await store.FindByIdentityAsync(issuer!, subject!, cancellationToken))?.CustomerId
            ?? throw new InvalidOperationException("A customer insert conflicted, but no customer has this identity.");
    }
}
