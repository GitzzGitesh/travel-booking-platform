using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>
/// The composed flight providers, by <see cref="IFlightProvider.Id"/> (ADR 0004). An offer, a selection or a booking is
/// always handled by the provider that made it: <see cref="ProviderOfferRef.ProviderId"/> resolves it, and an id that
/// is not composed never falls through to another provider. Search goes to one provider:
/// <c>Flights:SearchProviderId</c> when several are composed, otherwise the only one. Searching several providers at
/// once (fan-out) is not built. An operation an adapter does not implement is never called (Q6 capabilities).
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

    /// <summary>The provider that owns this id, if it is composed AND implements the operation; otherwise null.</summary>
    public IFlightProvider? FindFor(string providerId, ProviderOperation operation) =>
        Find(providerId) is { } provider && provider.Capabilities.Implements(operation) ? provider : null;

    /// <summary>The provider searches go to.</summary>
    public IFlightProvider ForSearch => SearchProviderProblem() is { } problem
        ? throw new InvalidOperationException(problem)
        : _searchProviderId is { } configured ? _byId[configured] : _byId.Values.Single();

    /// <summary>Why searching is not possible with this composition, or null when it is. Checked at startup.</summary>
    public string? SearchProviderProblem()
    {
        if (_searchProviderId is { } configured)
        {
            if (Find(configured) is not { } chosen)
            {
                return $"{SearchProviderSetting} names '{configured}', which is not composed.";
            }

            return chosen.Capabilities.Implements(ProviderOperation.Search)
                ? null
                : $"{SearchProviderSetting} names '{configured}', whose adapter does not implement search yet (stage {chosen.Capabilities.Stage}).";
        }

        return _byId.Count switch
        {
            0 => "No flight provider is composed.",
            1 => _byId.Values.Single().Capabilities.Implements(ProviderOperation.Search)
                ? null
                : $"The only flight provider, '{_byId.Keys.Single()}', does not implement search yet.",
            _ => $"Several flight providers are composed: set {SearchProviderSetting} (searching more than one is not built).",
        };
    }
}
