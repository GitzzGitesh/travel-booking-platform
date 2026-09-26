using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Sabre;

/// <summary>
/// Sabre adapter, SCAFFOLDED (Q6 candidate): configuration, credential contract, capability declaration and the shared
/// HTTP and error-mapping structure only. No Sabre request or response is mapped. Sabre's token scheme and API schemas
/// need agency (PCC) access and the supplier's documentation, so every operation refuses without sending anything, and
/// the core never calls them (<see cref="Capabilities"/>): an enabled Sabre adapter cannot be chosen for search.
/// </summary>
internal sealed class SabreFlightProvider(FlightProviderCapabilities capabilities) : IFlightProvider
{
    public const string ProviderId = "sabre";

    public string Id => ProviderId;

    public FlightProviderCapabilities Capabilities => capabilities;

    public Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightSearchResult, ProviderError>.Failure(NotImplemented("search")));

    public Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightOffer, ProviderError>.Failure(NotImplemented("revalidation")));

    public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingConfirmation, ProviderError>.Failure(NotImplemented("booking")));

    public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingLookup, ProviderError>.Failure(NotImplemented("booking lookup")));

    // Nothing is sent: a definitive refusal, never an unknown outcome.
    private static ProviderError NotImplemented(string operation) =>
        new(ProviderErrorKind.InvalidRequest, $"Sabre {operation} is not implemented: the adapter is scaffolded until Sabre access and API confirmation (Q6).");
}

/// <summary>
/// <c>Integrations:Flights:Sabre</c>. Credentials come from user-secrets / Key Vault only. The fields are the ones a
/// Sabre agency integration is expected to need; the exact authentication scheme requires supplier confirmation.
/// </summary>
public sealed class SabreOptions : SupplierHttpOptions
{
    public const string SectionName = "Integrations:Flights:Sabre";

    /// <summary>The API user (client) id issued for our agency.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The agency's pseudo city code (PCC).</summary>
    public string? PseudoCityCode { get; set; }

    public override IEnumerable<string> Problems()
    {
        foreach (var problem in base.Problems())
        {
            yield return problem;
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(PseudoCityCode))
        {
            yield return "ClientId, ClientSecret (secrets) and PseudoCityCode are required.";
        }
    }
}

public static class SabreRegistration
{
    private const string _gds = "A standard GDS capability; availability depends on our PCC, agreements and the API products purchased";

    /// <summary>What Sabre supports, as far as verified: nothing is verified yet. Revise by configuration after verification.</summary>
    internal static FlightProviderCapabilities Declared { get; } = new(
        AdapterStage.Scaffolded,
        [],
        Enum.GetValues<FlightCapability>().ToDictionary(c => c, c => c switch
        {
            FlightCapability.Search or FlightCapability.OneWay or FlightCapability.RoundTrip or FlightCapability.MultiCity
                or FlightCapability.Booking or FlightCapability.Ticketing or FlightCapability.Cancellation
                or FlightCapability.BookingLookupBySupplierReference => new CapabilityDeclaration(CapabilitySupport.RequiresConfirmation, _gds),
            FlightCapability.IdempotentBookingByOwnReference or FlightCapability.BookingLookupByOwnReference
                => new CapabilityDeclaration(CapabilitySupport.RequiresConfirmation, "Mandatory for reconciliation: confirm, or an ADR for keeping the PNR locator"),
            _ => new CapabilityDeclaration(CapabilitySupport.RequiresConfirmation),
        }));

    /// <summary>Composes the Sabre adapter only when <c>Enabled</c> is true; its settings are then validated at startup.</summary>
    public static IServiceCollection AddSabreFlightProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SabreOptions.SectionName);
        if (!section.GetValue<bool>(nameof(SupplierHttpOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<SabreOptions>()
            .Bind(section)
            .Validate(o => !o.Problems().Any(), $"{SabreOptions.SectionName} is incomplete: set BaseUrl (https), ClientId, ClientSecret and PseudoCityCode.")
            .ValidateOnStart();

        var capabilities = Declared.WithOverrides(section.GetSection("Capabilities").AsEnumerable(makePathsRelative: true));
        services.AddSingleton<IFlightProvider>(new SabreFlightProvider(capabilities));
        return services;
    }
}
