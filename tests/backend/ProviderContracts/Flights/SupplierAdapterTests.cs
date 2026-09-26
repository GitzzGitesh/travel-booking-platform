using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Integrations.Flights.Sabre;
using TravelBooking.Integrations.Flights.Travelport;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.ProviderContracts.Flights;

/// <summary>
/// The four candidate supplier adapters (Q6), tested against DOCUMENTATION-SHAPED fixtures through a fake HTTP handler.
/// These prove our mapping, error mapping and wiring, not the suppliers' real behaviour: that needs the credential-gated
/// sandbox contract classes below. Nothing here reaches a network.
/// </summary>
public sealed class SupplierAdapterTests
{
    private static readonly DateTimeOffset _now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly FlightSearchCriteria _oneWayWithInfant =
        new(new AirportCode("LHR"), new AirportCode("JFK"), new DateOnly(2027, 2, 14), null, new PassengerMix(1, 0, 1), CabinClass.Business);

    // ---------- Duffel ----------

    private const string _duffelOffer = """
        {"id":"off_0001","total_amount":"456.78","total_currency":"GBP","expires_at":"2027-01-15T09:30:00Z","owner":{"iata_code":"BA"},
         "conditions":{"refund_before_departure":{"allowed":true,"penalty_amount":"50.00","penalty_currency":"GBP"},
                       "change_before_departure":{"allowed":true,"penalty_amount":"0.00","penalty_currency":"GBP"}},
         "slices":[{"segments":[{"origin":{"iata_code":"LHR"},"destination":{"iata_code":"JFK"},
            "departing_at":"2027-02-14T10:00:00","arriving_at":"2027-02-14T12:55:00",
            "marketing_carrier":{"iata_code":"BA"},"marketing_carrier_flight_number":"117","operating_carrier":{"iata_code":"AA"},
            "duration":"PT7H55M",
            "passengers":[{"fare_basis_code":"OLOWGB","baggages":[{"type":"checked","quantity":1},{"type":"carry_on","quantity":1}]}]}]}]}
        """;

    [Fact]
    public async Task Duffel_search_maps_the_documented_offer_shape_into_the_port_model()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, $$$"""{"data":{"id":"orq_1","offers":[{{{_duffelOffer}}}]}}""");
        var provider = Duffel(handler);

        var offer = (await provider.SearchAsync(_oneWayWithInfant, Ct)).Value.Offers.ShouldHaveSingleItem();

        (offer.Reference, offer.TotalPrice, offer.ExpiresAt).ShouldBe((new ProviderOfferRef("duffel", "off_0001"), new Money(456.78m, new CurrencyCode("GBP")), _now.AddMinutes(30)));
        var segment = offer.Slices.Single().Segments.Single();
        (segment.MarketingCarrier, segment.FlightNumber, segment.OperatingCarrier, segment.FareBasis).ShouldBe(("BA", "BA117", "AA", "OLOWGB"));
        (segment.DepartureLocal, segment.Duration).ShouldBe((new DateTime(2027, 2, 14, 10, 0, 0), new TimeSpan(7, 55, 0)));
        segment.DepartureLocal.Kind.ShouldBe(DateTimeKind.Unspecified);
        offer.Fare.ValidatingCarrier.ShouldBe("BA");
        offer.Fare.Baggage.ShouldBe(new BaggageAllowance(1, 1));
        offer.Fare.Conditions.ShouldBe(new FareConditions(FareAllowance.AllowedWithFee, FareAllowance.Free));
        offer.Fare.PriceBreakdown.ShouldBeNull(); // Duffel states offer totals, not a per-type split: not claimed
    }

    [Fact]
    public async Task Duffel_search_sends_the_documented_request_with_version_and_bearer_token()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, """{"data":{"id":"orq_1","offers":[]}}""");

        (await Duffel(handler).SearchAsync(_oneWayWithInfant, Ct)).Value.Offers.ShouldBeEmpty(); // no availability is valid

        var sent = handler.Requests.ShouldHaveSingleItem();
        (sent.Method, sent.Path).ShouldBe(("POST", "/air/offer_requests?return_offers=true"));
        (sent.Authorization, sent.Headers["Duffel-Version"]).ShouldBe(("Bearer test-token", "v2"));
        var data = JsonDocument.Parse(sent.Body!).RootElement.GetProperty("data");
        data.GetProperty("cabin_class").GetString().ShouldBe("business");
        data.GetProperty("slices")[0].GetProperty("departure_date").GetString().ShouldBe("2027-02-14");
        data.GetProperty("passengers").EnumerateArray().Select(p => p.GetProperty("type").GetString()).ShouldBe(["adult", "infant_without_seat"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.AuthFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProviderErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderErrorKind.Unavailable)]
    public async Task Duffel_errors_map_to_the_shared_taxonomy(HttpStatusCode status, ProviderErrorKind expected)
    {
        var handler = new FakeHandler().Respond(status, """{"errors":[{"code":"some_supplier_code","message":"supplier detail","type":"x"}]}""");

        var result = await Duffel(handler).SearchAsync(_oneWayWithInfant, Ct);

        result.Error.Kind.ShouldBe(expected);
        result.Error.Message.ShouldNotContain("supplier detail"); // supplier messages never cross the adapter
    }

    [Fact]
    public async Task A_duffel_response_that_does_not_match_the_documented_shape_is_unavailable_not_an_exception()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, """{"data":{"id":"orq_1","offers":[{"id":"off_1"}]}}""");

        (await Duffel(handler).SearchAsync(_oneWayWithInfant, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    [Fact]
    public async Task Duffel_revalidation_gets_the_offer_again_and_refuses_foreign_references_unsent()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, $$"""{"data":{{_duffelOffer}}}""");
        var provider = Duffel(handler);

        (await provider.RevalidateAsync(new ProviderOfferRef("duffel", "off_0001"), Ct)).Value.TotalPrice.Amount.ShouldBe(456.78m);
        (await provider.RevalidateAsync(new ProviderOfferRef("amadeus", "off_0001"), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
        (await provider.RevalidateAsync(new ProviderOfferRef("duffel", "../orders"), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);

        handler.Requests.ShouldHaveSingleItem().Path.ShouldBe("/air/offers/off_0001");
    }

    [Fact]
    public async Task A_duffel_search_that_times_out_is_unavailable()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromSeconds(5) }.Respond(HttpStatusCode.OK, """{"data":{"id":"orq_1","offers":[]}}""");

        (await Duffel(handler, searchTimeout: TimeSpan.FromMilliseconds(100)).SearchAsync(_oneWayWithInfant, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.Unavailable);
    }

    // ---------- Amadeus ----------

    private const string _amadeusOffer = """
        {"type":"flight-offer","id":"1","source":"GDS","lastTicketingDate":"2027-01-20",
         "itineraries":[{"duration":"PT7H55M","segments":[{"id":"1","departure":{"iataCode":"LHR","at":"2027-02-14T10:00:00"},
            "arrival":{"iataCode":"JFK","at":"2027-02-14T12:55:00"},"carrierCode":"BA","number":"117","operating":{"carrierCode":"AA"},"duration":"PT7H55M"}]}],
         "price":{"currency":"EUR","total":"500.00","base":"400.00","grandTotal":"500.00"},
         "validatingAirlineCodes":["BA"],
         "travelerPricings":[
            {"travelerId":"1","travelerType":"ADULT","price":{"currency":"EUR","total":"450.00","base":"360.00"},
             "fareDetailsBySegment":[{"segmentId":"1","cabin":"BUSINESS","fareBasis":"JLOWGB","includedCheckedBags":{"quantity":2}}]},
            {"travelerId":"2","travelerType":"HELD_INFANT","price":{"currency":"EUR","total":"50.00","base":"40.00"},
             "fareDetailsBySegment":[{"segmentId":"1","cabin":"BUSINESS","fareBasis":"JLOWGBIN","includedCheckedBags":{"quantity":1}}]}]}
        """;

    [Fact]
    public async Task Amadeus_search_maps_the_documented_offer_shape_with_a_per_traveler_breakdown()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, _token).Respond(HttpStatusCode.OK, $$"""{"data":[{{_amadeusOffer}}]}""");

        var offer = (await Amadeus(handler).SearchAsync(_oneWayWithInfant, Ct)).Value.Offers.ShouldHaveSingleItem();

        (offer.TotalPrice, offer.ExpiresAt).ShouldBe((new Money(500m, new CurrencyCode("EUR")), _now.AddMinutes(15))); // our configured lifetime
        offer.Reference.ProviderId.ShouldBe("amadeus");
        JsonDocument.Parse(offer.Reference.Value).RootElement.GetProperty("id").GetString().ShouldBe("1"); // the offer itself, for pricing
        var breakdown = offer.Fare.PriceBreakdown.ShouldNotBeNull();
        breakdown.Total.ShouldBe(offer.TotalPrice);
        breakdown.Passengers.Select(p => (p.Type, p.BaseFare.Amount, p.TaxesAndFees.Amount)).ShouldBe([(PassengerType.Adult, 360m, 90m), (PassengerType.Infant, 40m, 10m)]);
        (offer.Fare.ValidatingCarrier, offer.Fare.TicketingDeadline).ShouldBe(("BA", new DateTimeOffset(2027, 1, 20, 0, 0, 0, TimeSpan.Zero)));
        (offer.Fare.Baggage, offer.Fare.Conditions).ShouldBe((null, FareConditions.NotStated)); // not stated in the mapped fields
        var segment = offer.Slices.Single().Segments.Single();
        (segment.FlightNumber, segment.OperatingCarrier, segment.FareBasis, segment.Duration).ShouldBe(("BA117", "AA", "JLOWGB", new TimeSpan(7, 55, 0)));
    }

    [Fact]
    public async Task Amadeus_gets_one_token_for_several_calls_and_sends_the_documented_search()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, _token).Respond(HttpStatusCode.OK, """{"data":[]}""").Respond(HttpStatusCode.OK, """{"data":[]}""");
        var provider = Amadeus(handler);

        await provider.SearchAsync(_oneWayWithInfant, Ct);
        await provider.SearchAsync(_oneWayWithInfant, Ct);

        handler.Requests.Select(r => r.Path).ShouldBe(["/v1/security/oauth2/token", "/v2/shopping/flight-offers", "/v2/shopping/flight-offers"]);
        handler.Requests[0].Body.ShouldBe("grant_type=client_credentials&client_id=test-id&client_secret=test-secret");
        var search = handler.Requests[1];
        (search.Authorization, search.Headers["X-HTTP-Method-Override"]).ShouldBe(("Bearer tok-1", "GET"));
        var body = JsonDocument.Parse(search.Body!).RootElement;
        body.GetProperty("travelers").EnumerateArray().Select(t => (t.GetProperty("travelerType").GetString(), t.TryGetProperty("associatedAdultId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null))
            .ShouldBe([("ADULT", null), ("HELD_INFANT", "1")]);
        body.GetProperty("searchCriteria").GetProperty("flightFilters").GetProperty("cabinRestrictions")[0].GetProperty("cabin").GetString().ShouldBe("BUSINESS");
    }

    [Fact]
    public async Task Amadeus_revalidation_prices_the_stored_offer_itself()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.OK, _token).Respond(HttpStatusCode.OK, $$$"""{"data":{"type":"flight-offers-pricing","flightOffers":[{{{_amadeusOffer}}}]}}""");
        var reference = new ProviderOfferRef("amadeus", JsonDocument.Parse(_amadeusOffer).RootElement.GetRawText());

        var repriced = (await Amadeus(handler).RevalidateAsync(reference, Ct)).Value;

        repriced.TotalPrice.Amount.ShouldBe(500m);
        var pricing = handler.Requests[1];
        pricing.Path.ShouldBe("/v1/shopping/flight-offers/pricing");
        var sentOffer = JsonDocument.Parse(pricing.Body!).RootElement.GetProperty("data").GetProperty("flightOffers")[0];
        sentOffer.GetProperty("id").GetString().ShouldBe("1");
        (await Amadeus(new FakeHandler()).RevalidateAsync(new ProviderOfferRef("amadeus", "not json"), Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task A_rejected_amadeus_token_is_replaced_once_and_bad_credentials_are_an_auth_failure()
    {
        var retried = new FakeHandler().Respond(HttpStatusCode.OK, _token).Respond(HttpStatusCode.Unauthorized, "{}")
            .Respond(HttpStatusCode.OK, _token).Respond(HttpStatusCode.OK, """{"data":[]}""");
        (await Amadeus(retried).SearchAsync(_oneWayWithInfant, Ct)).IsSuccess.ShouldBeTrue();
        retried.Requests.Count(r => r.Path == "/v1/security/oauth2/token").ShouldBe(2);

        var badCredentials = new FakeHandler().Respond(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        (await Amadeus(badCredentials).SearchAsync(_oneWayWithInfant, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.AuthFailure);
    }

    // ---------- Booking is not implemented anywhere yet: refused, never sent ----------

    [Fact]
    public async Task No_candidate_adapter_sends_a_booking_or_lookup()
    {
        var handler = new FakeHandler();
        var details = new FlightBookingDetails(new ClientReference("item-1"), new ProviderOfferRef("x", "y"), new Money(1m, new CurrencyCode("USD")), [new FlightPassenger(PassengerType.Adult, "Test", "Traveller")]);

        foreach (var provider in new[] { Duffel(handler), Amadeus(handler), Composed("Sabre", _sabreSettings)!, Composed("Travelport", _travelportSettings)! })
        {
            (await provider.BookAsync(details, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest, provider.Id);
            (await provider.RetrieveBookingAsync(details.ClientReference, Ct)).Error.Kind.ShouldBe(ProviderErrorKind.InvalidRequest, provider.Id);
            provider.Capabilities.Implements(ProviderOperation.Book).ShouldBeFalse(provider.Id);
        }

        handler.Requests.ShouldBeEmpty();
    }

    // ---------- Registration, configuration and capabilities ----------

    [Theory]
    [InlineData("Amadeus")]
    [InlineData("Duffel")]
    [InlineData("Sabre")]
    [InlineData("Travelport")]
    public void An_adapter_is_composed_only_when_enabled(string supplier) =>
        Composed(supplier, []).ShouldBeNull();

    [Theory]
    [InlineData("Amadeus")]
    [InlineData("Duffel")]
    [InlineData("Sabre")]
    [InlineData("Travelport")]
    public void An_enabled_adapter_without_its_credentials_fails_at_startup(string supplier) =>
        Should.Throw<OptionsValidationException>(() => Composed(supplier, new() { [$"Integrations:Flights:{supplier}:Enabled"] = "true", [$"Integrations:Flights:{supplier}:BaseUrl"] = "https://sandbox.example.test/" }));

    [Fact]
    public void An_http_base_url_is_refused() =>
        Should.Throw<OptionsValidationException>(() => Composed("Duffel", _duffelSettings.With("Integrations:Flights:Duffel:BaseUrl", "http://sandbox.example.test/")));

    [Fact]
    public void Every_candidate_declares_every_capability_and_its_stage_honestly()
    {
        var providers = new[] { Duffel(new FakeHandler()), Amadeus(new FakeHandler()), Composed("Sabre", _sabreSettings)!, Composed("Travelport", _travelportSettings)! };

        foreach (var provider in providers)
        {
            Enum.GetValues<FlightCapability>().ShouldAllBe(c => provider.Capabilities.Declarations.ContainsKey(c), provider.Id);
            provider.Capabilities.Stage.ShouldNotBe(AdapterStage.ProductionReady, provider.Id); // nothing is production ready
            provider.Capabilities.Stage.ShouldNotBe(AdapterStage.SandboxVerified, provider.Id); // nothing is sandbox-verified
        }

        providers.Where(p => p.Capabilities.Stage == AdapterStage.Scaffolded).Select(p => p.Id).ShouldBe(["sabre", "travelport"]);
        providers.Where(p => p.Capabilities.Stage == AdapterStage.Scaffolded).ShouldAllBe(p => p.Capabilities.Implemented.Count == 0);
        providers.ShouldAllBe(p => p.Capabilities.Get(FlightCapability.IdempotentBookingByOwnReference).Support == CapabilitySupport.RequiresConfirmation);
    }

    [Fact]
    public void Capabilities_are_revised_by_configuration_after_verification()
    {
        var provider = Composed("Duffel", _duffelSettings.With("Integrations:Flights:Duffel:Capabilities:SeatSelection", "Supported"))!;

        provider.Capabilities.Get(FlightCapability.SeatSelection).Support.ShouldBe(CapabilitySupport.Supported);
        Should.Throw<ArgumentException>(() => Composed("Duffel", _duffelSettings.With("Integrations:Flights:Duffel:Capabilities:Teleport", "Supported")));
    }

    // ---------- helpers ----------

    private const string _token = """{"type":"amadeusOAuth2Token","access_token":"tok-1","expires_in":1799,"token_type":"Bearer"}""";

    private static readonly Dictionary<string, string?> _duffelSettings = new()
    {
        ["Integrations:Flights:Duffel:Enabled"] = "true",
        ["Integrations:Flights:Duffel:BaseUrl"] = "https://duffel.example.test/",
        ["Integrations:Flights:Duffel:AccessToken"] = "test-token",
        ["Integrations:Flights:Duffel:ApiVersion"] = "v2",
    };

    private static readonly Dictionary<string, string?> _amadeusSettings = new()
    {
        ["Integrations:Flights:Amadeus:Enabled"] = "true",
        ["Integrations:Flights:Amadeus:BaseUrl"] = "https://amadeus.example.test/",
        ["Integrations:Flights:Amadeus:ClientId"] = "test-id",
        ["Integrations:Flights:Amadeus:ClientSecret"] = "test-secret",
    };

    private static readonly Dictionary<string, string?> _sabreSettings = new()
    {
        ["Integrations:Flights:Sabre:Enabled"] = "true",
        ["Integrations:Flights:Sabre:BaseUrl"] = "https://sabre.example.test/",
        ["Integrations:Flights:Sabre:ClientId"] = "test-id",
        ["Integrations:Flights:Sabre:ClientSecret"] = "test-secret",
        ["Integrations:Flights:Sabre:PseudoCityCode"] = "TEST",
    };

    private static readonly Dictionary<string, string?> _travelportSettings = new()
    {
        ["Integrations:Flights:Travelport:Enabled"] = "true",
        ["Integrations:Flights:Travelport:BaseUrl"] = "https://travelport.example.test/",
        ["Integrations:Flights:Travelport:ClientId"] = "test-id",
        ["Integrations:Flights:Travelport:ClientSecret"] = "test-secret",
        ["Integrations:Flights:Travelport:AccessGroup"] = "TEST",
    };

    private static IFlightProvider Duffel(FakeHandler handler, TimeSpan? searchTimeout = null) =>
        Composed("Duffel", searchTimeout is { } timeout ? _duffelSettings.With("Integrations:Flights:Duffel:SearchTimeout", timeout.ToString()) : _duffelSettings, handler)!;

    private static IFlightProvider Amadeus(FakeHandler handler) => Composed("Amadeus", _amadeusSettings, handler)!;

    /// <summary>The adapter as its registration composes it, with a fake transport; null when it is not composed.</summary>
    private static IFlightProvider? Composed(string supplier, Dictionary<string, string?> settings, FakeHandler? handler = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
        _ = supplier switch
        {
            "Amadeus" => services.AddAmadeusFlightProvider(configuration),
            "Duffel" => services.AddDuffelFlightProvider(configuration),
            "Sabre" => services.AddSabreFlightProvider(configuration),
            "Travelport" => services.AddTravelportFlightProvider(configuration),
            _ => throw new ArgumentOutOfRangeException(nameof(supplier)),
        };

        if (handler is not null)
        {
            services.AddHttpClient($"Integrations.Flights.{supplier}").ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        var provider = services.BuildServiceProvider();
        foreach (var validate in new Action[]
        {
            () => _ = provider.GetService<IOptions<AmadeusOptions>>()?.Value,
            () => _ = provider.GetService<IOptions<DuffelOptions>>()?.Value,
            () => _ = provider.GetService<IOptions<SabreOptions>>()?.Value,
            () => _ = provider.GetService<IOptions<TravelportOptions>>()?.Value,
        })
        {
            if (services.Any(d => d.ServiceType == typeof(IFlightProvider)))
            {
                validate(); // startup validation (ValidateOnStart runs in a host; here it is triggered directly)
            }
        }

        return provider.GetServices<IFlightProvider>().SingleOrDefault();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>A fake transport: queued responses, and a record of every request (path, headers, body).</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<SentRequest> Requests { get; } = [];

    public TimeSpan Delay { get; init; }

    public FakeHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new SentRequest(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("No response queued: an unexpected supplier call.");
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal sealed record SentRequest(string Method, string Path, string? Authorization, Dictionary<string, string> Headers, string? Body);

internal static class SettingsExtensions
{
    public static Dictionary<string, string?> With(this Dictionary<string, string?> settings, string key, string value) =>
        new(settings) { [key] = value };
}
