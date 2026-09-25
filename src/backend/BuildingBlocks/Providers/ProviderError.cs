namespace TravelBooking.BuildingBlocks.Providers;

/// <summary>
/// The error taxonomy every provider port returns (ADR 0004, docs/architecture/provider-integration.md).
/// Supplier error codes never cross the adapter boundary.
/// </summary>
public enum ProviderErrorKind
{
    PriceChanged,
    OfferExpired,
    SoldOut,
    InvalidRequest,
    Rejected,
    Unknown,
    Unavailable,
    RateLimited,
    AuthFailure,
}

/// <param name="Message">Safe for logs: never a supplier payload or personal data.</param>
public sealed record ProviderError(ProviderErrorKind Kind, string Message);
