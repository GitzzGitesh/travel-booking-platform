using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>
/// The composed flight providers, by <see cref="IFlightProvider.Id"/> (ADR 0004). An offer, a selection or a booking is
/// always handled by the provider that made it: <see cref="ProviderOfferRef.ProviderId"/> resolves it. Search goes to one
/// provider: <c>Flights:SearchProviderId</c> when several are composed (a real supplier next to the mock), otherwise the only
/// one. Searching several providers at once (fan-out) is not built.
/// </summary>
internal sealed class FlightProviders
{
    public const string SearchProviderSetting = "Flights:SearchProviderId";

    private readonly Dictionary<string, IFlightProvider> _byId;
    private readonly string? _searchProviderId;

    public FlightProviders(IEnumerable<IFlightProvider> providers, string? searchProviderId = null)
    {
        var list = providers.ToList();
        if (list.GroupBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"Two flight providers share the id '{duplicate.Key}'.");
        }

        _byId = list.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _searchProviderId = string.IsNullOrWhiteSpace(searchProviderId) ? null : searchProviderId;
    }

    /// <summary>The provider that owns this id; null when it is not composed here (e.g. removed from configuration).</summary>
    public IFlightProvider? Find(string providerId) => _byId.GetValueOrDefault(providerId);

    /// <summary>The provider searches go to.</summary>
    public IFlightProvider ForSearch => _searchProviderId is { } configured
        ? Find(configured) ?? throw new InvalidOperationException($"{SearchProviderSetting} names '{configured}', which is not composed.")
        : _byId.Count == 1
            ? _byId.Values.Single()
            : throw new InvalidOperationException(_byId.Count == 0
                ? "No flight provider is composed."
                : $"Several flight providers are composed: set {SearchProviderSetting} (searching more than one is not built).");
}
