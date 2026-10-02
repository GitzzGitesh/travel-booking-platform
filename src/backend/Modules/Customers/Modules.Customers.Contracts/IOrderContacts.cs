namespace TravelBooking.Modules.Customers.Contracts;

/// <summary>
/// The booker's contact for an order's customer notices (ADR 0024): read when a message is sent, never stored by the
/// caller (Q9, ADR 0020). Null once the order's personal data is anonymised, or when no contact was given.
/// </summary>
public interface IOrderContacts
{
    Task<OrderContact?> FindForNoticeAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>Personal data: never logged or stored outside Customers.</summary>
public sealed record OrderContact(string Email);
