using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

/// <summary>Test doubles opt in to every operation explicitly: a provider that declares nothing implements nothing.</summary>
internal static class TestCapabilities
{
    public static readonly FlightProviderCapabilities All = new(
        AdapterStage.Mock,
        [ProviderOperation.Search, ProviderOperation.Revalidate, ProviderOperation.Book, ProviderOperation.RetrieveBooking],
        new Dictionary<FlightCapability, CapabilityDeclaration>());
}
