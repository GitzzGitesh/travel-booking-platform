using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Integrations.Flights.Amadeus.Dtos;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Amadeus;

/// <summary>
/// Amadeus Self-Service adapter (Q6 candidate). Search (Flight Offers Search) and revalidation (Flight Offers Price) are
/// mapped from the public documentation and tested against documentation-shaped fixtures only. Booking and lookup are
/// NOT implemented: production ticketing needs a consolidator agreement, and lookup by our own reference is not a
/// documented feature, so the adapter would have to keep the Amadeus order id before returning, which needs a
/// supplier ADR (provider-integration.md). The core never calls them (<see cref="Capabilities"/>).
/// </summary>
internal sealed partial class AmadeusFlightProvider : IFlightProvider
{
    public const string ProviderId = "amadeus";
    public const string HttpClientName = "Integrations.Flights.Amadeus";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions _tokenJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptions<AmadeusOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AmadeusFlightProvider> _logger;
    private readonly AccessTokenCache _tokens;

    public AmadeusFlightProvider(IHttpClientFactory httpClients, IOptions<AmadeusOptions> options, FlightProviderCapabilities capabilities, TimeProvider timeProvider, ILogger<AmadeusFlightProvider> logger)
    {
        _httpClients = httpClients;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        Capabilities = capabilities;
        _tokens = new AccessTokenCache(AcquireTokenAsync, timeProvider);
    }

    public string Id => ProviderId;

    public FlightProviderCapabilities Capabilities { get; }

    public async Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            () => Post("v2/shopping/flight-offers", AmadeusMapping.ToSearchRequest(criteria, _options.Value.MaxOffers)),
            _options.Value.SearchTimeout,
            "search",
            cancellationToken);

        return response.IsSuccess
            ? Map(response.Value, body => new FlightSearchResult(OffersIn(JsonDocument.Parse(body).RootElement.GetProperty("data"))))
            : Result<FlightSearchResult, ProviderError>.Failure(response.Error);
    }

    public async Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken)
    {
        if (offer.ProviderId != ProviderId || ParseOffer(offer.Value) is not { } raw)
        {
            return Result<FlightOffer, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "Not an Amadeus offer reference."));
        }

        var response = await SendAsync(
            () => Post("v1/shopping/flight-offers/pricing", new AmadeusPricingRequest(new AmadeusPricingData("flight-offers-pricing", [raw]))),
            _options.Value.ReadTimeout,
            "revalidate",
            cancellationToken);

        return response.IsSuccess
            ? Map(response.Value, body => OffersIn(JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("flightOffers")).Single())
            : Result<FlightOffer, ProviderError>.Failure(response.Error);
    }

    public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingConfirmation, ProviderError>.Failure(NotImplemented("booking")));

    public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingLookup, ProviderError>.Failure(NotImplemented("booking lookup")));

    private static ProviderError NotImplemented(string operation) =>
        new(ProviderErrorKind.InvalidRequest, $"Amadeus {operation} is not implemented until a consolidator agreement, sandbox verification and a supplier ADR (Q6).");

    private List<FlightOffer> OffersIn(JsonElement offers)
    {
        var expiresAt = _timeProvider.GetUtcNow() + _options.Value.OfferLifetime;
        return offers.EnumerateArray()
            .Select(raw => AmadeusMapping.ToOffer(raw, raw.Deserialize<AmadeusOffer>(Json) ?? throw new JsonException("Empty Amadeus offer."), ProviderId, expiresAt))
            .ToList();
    }

    private static JsonElement? ParseOffer(string value)
    {
        try
        {
            var element = JsonDocument.Parse(value).RootElement;
            return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Amadeus takes these searches as POST with a method-override header (documented for the POST form of search).
    private static HttpRequestMessage Post<T>(string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("X-HTTP-Method-Override", "GET");
        return request;
    }

    // A 401 means the cached token was revoked or expired early: forget it and try once more with a new one (a read).
    private async Task<Result<SupplierResponse, ProviderError>> SendAsync(Func<HttpRequestMessage> build, TimeSpan timeout, string operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await _tokens.GetAsync(cancellationToken);
            if (!token.IsSuccess)
            {
                LogFailure(_logger, ProviderId, "authenticate", token.Error.Kind);
                return Result<SupplierResponse, ProviderError>.Failure(token.Error);
            }

            using var request = build();
            request.Headers.Authorization = SupplierHttp.Bearer(token.Value);
            var response = await SupplierHttp.SendAsync(_httpClients.CreateClient(HttpClientName), request, timeout, SupplierCallKind.Read, cancellationToken);
            if (!response.IsSuccess && response.Error.Kind == ProviderErrorKind.AuthFailure && attempt == 1)
            {
                _tokens.Invalidate();
                continue;
            }

            if (!response.IsSuccess)
            {
                LogFailure(_logger, ProviderId, operation, response.Error.Kind);
            }

            return response;
        }
    }

    private async Task<Result<AccessToken, ProviderError>> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/security/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
            }),
        };

        var response = await SupplierHttp.SendAsync(_httpClients.CreateClient(HttpClientName), request, options.ReadTimeout, SupplierCallKind.Read, cancellationToken);
        if (!response.IsSuccess)
        {
            // A refused token request means our credentials are wrong: an operator must act.
            return Result<AccessToken, ProviderError>.Failure(response.Error.Kind is ProviderErrorKind.InvalidRequest or ProviderErrorKind.AuthFailure
                ? response.Error with { Kind = ProviderErrorKind.AuthFailure }
                : response.Error);
        }

        var token = JsonSerializer.Deserialize<AmadeusToken>(response.Value.Body, _tokenJson);
        return token is { AccessToken.Length: > 0, ExpiresIn: > 0 }
            ? Result<AccessToken, ProviderError>.Success(new AccessToken(token.AccessToken, _timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn)))
            : Result<AccessToken, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Unexpected Amadeus token response."));
    }

    private Result<T, ProviderError> Map<T>(SupplierResponse response, Func<string, T> map)
        where T : notnull
    {
        try
        {
            return Result<T, ProviderError>.Success(map(response.Body));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            LogUnmappable(_logger, ProviderId, exception.GetType().Name);
            return Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Unexpected Amadeus response shape."));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight provider {ProviderId} {Operation} failed: {ErrorKind}")]
    private static partial void LogFailure(ILogger logger, string providerId, string operation, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight provider {ProviderId} returned a response the adapter cannot map ({ExceptionType})")]
    private static partial void LogUnmappable(ILogger logger, string providerId, string exceptionType);
}
