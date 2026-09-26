namespace TravelBooking.Modules.Flights.Ports;

/// <summary>What a supplier offers, in supplier-neutral terms (Q6). Declared per provider; never inferred.</summary>
public enum FlightCapability
{
    Search,
    OneWay,
    RoundTrip,
    MultiCity,
    PassengerTypes,
    CabinSelection,
    Baggage,
    FareConditions,
    BrandedFares,
    Revalidation,
    Booking,
    BookingLookupByOwnReference,
    BookingLookupBySupplierReference,
    IdempotentBookingByOwnReference,
    Cancellation,
    Refunds,
    Exchanges,
    ScheduleChangeNotifications,
    Ticketing,
    Ancillaries,
    SeatSelection,
    RequestedCurrency,
    MarketCoverage,
}

/// <summary>Whether a supplier supports a capability, as far as we have verified it.</summary>
public enum CapabilitySupport
{
    /// <summary>Not verified with the supplier (documentation, sandbox or contract): never assume either way.</summary>
    RequiresConfirmation,
    Supported,
    Unsupported,
}

/// <summary>How far our adapter for a supplier has got. Only <see cref="ProductionReady"/> may run in Production.</summary>
public enum AdapterStage
{
    /// <summary>The deterministic test double (Development and Staging only).</summary>
    Mock,

    /// <summary>Project, configuration, authentication and error-mapping structure only; no supplier mapping yet.</summary>
    Scaffolded,

    /// <summary>Some operations mapped from the supplier's public documentation, checked against fixtures only.</summary>
    MappedFromDocumentation,

    /// <summary>Mapped operations pass the provider contract suite against the supplier's sandbox.</summary>
    SandboxVerified,

    /// <summary>Commercially approved, with a supplier ADR, and verified for production.</summary>
    ProductionReady,
}

/// <summary>The port operations an adapter actually implements. The core never calls one that is not implemented.</summary>
public enum ProviderOperation
{
    Search,
    Revalidate,
    Book,
    RetrieveBooking,
}

/// <param name="Note">Why, or what must be confirmed (e.g. "needs a consolidator agreement for ticketing").</param>
public sealed record CapabilityDeclaration(CapabilitySupport Support, string? Note = null);

/// <summary>
/// A provider's declared capabilities, its adapter stage and the operations it implements. Declarations start in the
/// adapter's code and can be revised by configuration (<c>Integrations:Flights:&lt;Provider&gt;:Capabilities:&lt;Name&gt;</c>)
/// after commercial or API verification, without a code change. An undeclared capability requires confirmation.
/// </summary>
public sealed record FlightProviderCapabilities
{
    /// <summary>
    /// For a provider that declares nothing: it implements NO operation (fail closed), so an adapter that forgets to
    /// declare its capabilities is never called.
    /// </summary>
    public static readonly FlightProviderCapabilities NotDeclared = new(AdapterStage.Scaffolded, [], new Dictionary<FlightCapability, CapabilityDeclaration>());

    public FlightProviderCapabilities(AdapterStage stage, IReadOnlyCollection<ProviderOperation> implemented, IReadOnlyDictionary<FlightCapability, CapabilityDeclaration> declarations)
    {
        Stage = stage;
        Implemented = implemented.ToHashSet();
        Declarations = declarations;
    }

    public AdapterStage Stage { get; }

    public IReadOnlySet<ProviderOperation> Implemented { get; }

    public IReadOnlyDictionary<FlightCapability, CapabilityDeclaration> Declarations { get; }

    public bool Implements(ProviderOperation operation) => Implemented.Contains(operation);

    public CapabilityDeclaration Get(FlightCapability capability) =>
        Declarations.GetValueOrDefault(capability) ?? new CapabilityDeclaration(CapabilitySupport.RequiresConfirmation);

    /// <summary>
    /// These declarations revised by configuration entries (capability name → Supported, Unsupported or
    /// RequiresConfirmation). An unknown name or value is a configuration error, reported rather than ignored.
    /// </summary>
    public FlightProviderCapabilities WithOverrides(IEnumerable<KeyValuePair<string, string?>> overrides)
    {
        var declarations = new Dictionary<FlightCapability, CapabilityDeclaration>(Declarations);
        foreach (var (name, value) in overrides)
        {
            if (value is null)
            {
                continue;
            }

            if (!Enum.TryParse<FlightCapability>(name, ignoreCase: false, out var capability) || !Enum.IsDefined(capability))
            {
                throw new ArgumentException($"'{name}' is not a flight capability.", nameof(overrides));
            }

            if (!Enum.TryParse<CapabilitySupport>(value, ignoreCase: false, out var support) || !Enum.IsDefined(support))
            {
                throw new ArgumentException($"'{value}' is not a capability support value for {name}.", nameof(overrides));
            }

            declarations[capability] = new CapabilityDeclaration(support, "Revised by configuration");
        }

        return new FlightProviderCapabilities(Stage, Implemented, declarations);
    }
}
