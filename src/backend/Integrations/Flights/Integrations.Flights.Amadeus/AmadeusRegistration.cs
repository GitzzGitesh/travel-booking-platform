using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Amadeus;

/// <summary>
/// <c>Integrations:Flights:Amadeus</c>. The API key and secret come from user-secrets / Key Vault only (security rules).
/// The base URL selects the test or production environment.
/// </summary>
public sealed class AmadeusOptions : SupplierHttpOptions
{
    public const string SectionName = "Integrations:Flights:Amadeus";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>How many offers a search asks for.</summary>
    public int MaxOffers { get; set; } = 50;

    /// <summary>
    /// How long we treat an Amadeus offer as bookable before it must be searched again. Amadeus states no offer expiry
    /// in the mapped fields; this is our policy, to confirm with the supplier.
    /// </summary>
    public TimeSpan OfferLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The ISO-4217 currency searches ask Amadeus to price in (<c>currencyCode</c>), or unset for the supplier's
    /// default. Which currency we settle with Amadeus in is commercial (Q6 blockers, ADR 0019); the charge currency is
    /// decided separately (Q5), and a supplier currency is never relabelled as another.
    /// </summary>
    public string? Currency { get; set; }

    public override IEnumerable<string> Problems()
    {
        foreach (var problem in base.Problems())
        {
            yield return problem;
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return "ClientId and ClientSecret are required (user-secrets or Key Vault).";
        }

        if (MaxOffers is < 1 or > 250)
        {
            yield return "MaxOffers must be between 1 and 250.";
        }

        if (Currency is not null && !CurrencyCode.IsValid(Currency))
        {
            yield return "Currency must be an ISO-4217 code (three upper-case letters) or unset.";
        }

        if (OfferLifetime <= TimeSpan.Zero || OfferLifetime > TimeSpan.FromHours(2))
        {
            yield return "OfferLifetime must be between 0 and 2 hours.";
        }
    }
}

public static class AmadeusRegistration
{
    /// <summary>What Amadeus Self-Service supports, as far as verified (ADR 0019: nothing is sandbox-verified yet). Revise by configuration after verification.</summary>
    internal static FlightProviderCapabilities Declared { get; } = new(
        AdapterStage.MappedFromDocumentation,
        [ProviderOperation.Search, ProviderOperation.Revalidate],
        new Dictionary<FlightCapability, CapabilityDeclaration>
        {
            [FlightCapability.Search] = new(CapabilitySupport.Supported, "Flight Offers Search (documented)"),
            [FlightCapability.OneWay] = new(CapabilitySupport.Supported),
            [FlightCapability.RoundTrip] = new(CapabilitySupport.Supported),
            [FlightCapability.MultiCity] = new(CapabilitySupport.Supported, "Several originDestinations (documented); our search does not send them yet"),
            [FlightCapability.PassengerTypes] = new(CapabilitySupport.Supported, "ADULT, CHILD, HELD_INFANT"),
            [FlightCapability.CabinSelection] = new(CapabilitySupport.Supported),
            [FlightCapability.Baggage] = new(CapabilitySupport.RequiresConfirmation, "Included checked bags are stated; cabin bags are not"),
            [FlightCapability.FareConditions] = new(CapabilitySupport.RequiresConfirmation, "Fare rules come from a separate call"),
            [FlightCapability.BrandedFares] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Revalidation] = new(CapabilitySupport.Supported, "Flight Offers Price (documented)"),
            [FlightCapability.Booking] = new(CapabilitySupport.RequiresConfirmation, "Flight Create Orders exists; production ticketing needs a consolidator agreement"),
            [FlightCapability.BookingLookupByOwnReference] = new(CapabilitySupport.RequiresConfirmation, "Not a documented feature; the adapter would keep the Amadeus order id (needs an ADR)"),
            [FlightCapability.BookingLookupBySupplierReference] = new(CapabilitySupport.RequiresConfirmation, "Flight Order Management by Amadeus order id"),
            [FlightCapability.IdempotentBookingByOwnReference] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Cancellation] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Refunds] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Exchanges] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.ScheduleChangeNotifications] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Ticketing] = new(CapabilitySupport.RequiresConfirmation, "Through a consolidator in production"),
            [FlightCapability.Ancillaries] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.SeatSelection] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.RequestedCurrency] = new(CapabilitySupport.RequiresConfirmation, "currencyCode is sent when Currency is configured (documented); confirm it is honoured for every fare and matches our settlement currency"),
            [FlightCapability.MarketCoverage] = new(CapabilitySupport.RequiresConfirmation, "Content and markets per commercial agreement"),
        });

    /// <summary>Composes the Amadeus adapter only when <c>Enabled</c> is true; its settings are then validated at startup.</summary>
    public static IServiceCollection AddAmadeusFlightProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AmadeusOptions.SectionName);
        if (!section.GetValue<bool>(nameof(SupplierHttpOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<AmadeusOptions>()
            .Bind(section)
            .Validate(o => !o.Problems().Any(), $"{AmadeusOptions.SectionName} is incomplete: set BaseUrl (https), ClientId and ClientSecret (secrets).")
            .ValidateOnStart();

        // Resilience hook: read-only retry and circuit breaker (Microsoft.Extensions.Http.Resilience, ADR 0003) go here.
        services.AddHttpClient(AmadeusFlightProvider.HttpClientName, (provider, client) =>
        {
            client.BaseAddress = provider.GetRequiredService<IOptions<AmadeusOptions>>().Value.BaseUrl;
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        var capabilities = Declared.WithOverrides(section.GetSection("Capabilities").AsEnumerable(makePathsRelative: true));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFlightProvider>(provider => new AmadeusFlightProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<AmadeusOptions>>(),
            capabilities,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AmadeusFlightProvider>>()));
        return services;
    }
}
