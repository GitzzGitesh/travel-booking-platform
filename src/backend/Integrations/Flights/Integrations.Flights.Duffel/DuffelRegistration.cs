using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Duffel;

/// <summary>
/// <c>Integrations:Flights:Duffel</c>. The access token comes from user-secrets / Key Vault only, never appsettings
/// (security rules). Duffel issues separate test and live tokens; the base URL is configuration.
/// </summary>
public sealed class DuffelOptions : SupplierHttpOptions
{
    public const string SectionName = "Integrations:Flights:Duffel";

    public string? AccessToken { get; set; }

    /// <summary>The API version header Duffel requires; set explicitly so an upgrade is a deliberate change.</summary>
    public string? ApiVersion { get; set; }

    public override IEnumerable<string> Problems()
    {
        foreach (var problem in base.Problems())
        {
            yield return problem;
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            yield return "AccessToken is required (user-secrets or Key Vault).";
        }

        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            yield return "ApiVersion is required.";
        }
    }
}

public static class DuffelRegistration
{
    /// <summary>
    /// What Duffel supports, as far as verified: documented behaviour our mapping relies on is Supported; the rest
    /// needs confirmation in the sandbox or commercially. Revise by configuration after verification (Capabilities:*).
    /// </summary>
    internal static FlightProviderCapabilities Declared { get; } = new(
        AdapterStage.MappedFromDocumentation,
        [ProviderOperation.Search, ProviderOperation.Revalidate],
        new Dictionary<FlightCapability, CapabilityDeclaration>
        {
            [FlightCapability.Search] = new(CapabilitySupport.Supported, "Offer requests (documented)"),
            [FlightCapability.OneWay] = new(CapabilitySupport.Supported, "One slice"),
            [FlightCapability.RoundTrip] = new(CapabilitySupport.Supported, "Two slices"),
            [FlightCapability.MultiCity] = new(CapabilitySupport.Supported, "Several slices (documented); our search does not send them yet"),
            [FlightCapability.PassengerTypes] = new(CapabilitySupport.Supported, "Adult, child and infant types; confirm child handling without ages"),
            [FlightCapability.CabinSelection] = new(CapabilitySupport.Supported),
            [FlightCapability.Baggage] = new(CapabilitySupport.Supported, "Included baggage per segment and passenger"),
            [FlightCapability.FareConditions] = new(CapabilitySupport.Supported, "Refund and change before departure, with penalties"),
            [FlightCapability.BrandedFares] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Revalidation] = new(CapabilitySupport.Supported, "Get offer (documented); confirm expired/sold-out responses"),
            [FlightCapability.Booking] = new(CapabilitySupport.RequiresConfirmation, "Orders API exists; payment by balance and our merchant model need confirmation"),
            [FlightCapability.BookingLookupByOwnReference] = new(CapabilitySupport.RequiresConfirmation, "Whether orders can be found by our reference (e.g. metadata) is unconfirmed"),
            [FlightCapability.BookingLookupBySupplierReference] = new(CapabilitySupport.RequiresConfirmation, "Get order by Duffel id"),
            [FlightCapability.IdempotentBookingByOwnReference] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Cancellation] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Refunds] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Exchanges] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.ScheduleChangeNotifications] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Ticketing] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.Ancillaries] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.SeatSelection] = new(CapabilitySupport.RequiresConfirmation),
            [FlightCapability.RequestedCurrency] = new(CapabilitySupport.RequiresConfirmation, "Offer currency rules need confirmation (Q5: supplier currency is not the charge currency)"),
            [FlightCapability.MarketCoverage] = new(CapabilitySupport.RequiresConfirmation, "Airline content per market is commercial"),
        });

    /// <summary>Composes the Duffel adapter only when <c>Enabled</c> is true; its settings are then validated at startup.</summary>
    public static IServiceCollection AddDuffelFlightProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DuffelOptions.SectionName);
        if (!section.GetValue<bool>(nameof(SupplierHttpOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<DuffelOptions>()
            .Bind(section)
            .Validate(o => !o.Problems().Any(), $"{DuffelOptions.SectionName} is incomplete: set BaseUrl (https), AccessToken (secret) and ApiVersion.")
            .ValidateOnStart();

        // One named client; the per-operation timeout is applied per call, so the client's own is only a backstop.
        // Resilience hook: a read-only retry and circuit breaker are added here with Microsoft.Extensions.Http.Resilience
        // (ADR 0003) before production; writes are never retried.
        services.AddHttpClient(DuffelFlightProvider.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DuffelOptions>>().Value;
            client.BaseAddress = options.BaseUrl;
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        var capabilities = Declared.WithOverrides(section.GetSection("Capabilities").AsEnumerable(makePathsRelative: true));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFlightProvider>(provider => new DuffelFlightProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<DuffelOptions>>(),
            capabilities,
            provider.GetRequiredService<ILogger<DuffelFlightProvider>>()));
        return services;
    }
}
