using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.BuildingBlocks.Providers.Http;
using TravelBooking.Integrations.Flights.Duffel.Dtos;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Duffel;

/// <summary>
/// Duffel adapter (Q6 candidate). Search and revalidation are mapped from Duffel's public documentation and tested
/// against documentation-shaped fixtures only. Booking and lookup are NOT implemented: they wait for sandbox
/// verification of Duffel's order, payment (balance) and reference behaviour and a supplier ADR, so the core never
/// calls them (<see cref="Capabilities"/>). Logs carry the provider, operation and error kind, never bodies.
/// </summary>
internal sealed partial class DuffelFlightProvider(
    IHttpClientFactory httpClients,
    IOptions<DuffelOptions> options,
    FlightProviderCapabilities capabilities,
    ILogger<DuffelFlightProvider> logger) : IFlightProvider
{
    public const string ProviderId = "duffel";
    public const string HttpClientName = "Integrations.Flights.Duffel";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Id => ProviderId;

    public FlightProviderCapabilities Capabilities => capabilities;

    public async Task<Result<FlightSearchResult, ProviderError>> SearchAsync(FlightSearchCriteria criteria, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "air/offer_requests?return_offers=true");
        request.Content = JsonContent.Create(new DuffelEnvelope<DuffelOfferRequest>(DuffelMapping.ToOfferRequest(criteria)), options: Json);

        var response = await Send(request, options.Value.SearchTimeout, "search", cancellationToken);
        if (!response.IsSuccess)
        {
            return Result<FlightSearchResult, ProviderError>.Failure(response.Error);
        }

        return Map(response.Value, body => new FlightSearchResult(
            (Deserialize<DuffelOfferRequestResult>(body).Offers ?? []).Select(o => DuffelMapping.ToOffer(o, ProviderId)).ToList()));
    }

    public async Task<Result<FlightOffer, ProviderError>> RevalidateAsync(ProviderOfferRef offer, CancellationToken cancellationToken)
    {
        if (offer.ProviderId != ProviderId || string.IsNullOrWhiteSpace(offer.Value) || offer.Value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            return Result<FlightOffer, ProviderError>.Failure(new ProviderError(ProviderErrorKind.InvalidRequest, "Not a Duffel offer reference."));
        }

        // Retrieving the offer again returns it as Duffel prices it now (documented; confirm expired-offer responses).
        using var request = Request(HttpMethod.Get, $"air/offers/{offer.Value}");
        var response = await Send(request, options.Value.ReadTimeout, "revalidate", cancellationToken);
        if (!response.IsSuccess)
        {
            return Result<FlightOffer, ProviderError>.Failure(response.Error);
        }

        return Map(response.Value, body => DuffelMapping.ToOffer(Deserialize<DuffelOffer>(body), ProviderId));
    }

    public Task<Result<FlightBookingConfirmation, ProviderError>> BookAsync(FlightBookingDetails details, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingConfirmation, ProviderError>.Failure(NotImplemented("booking")));

    public Task<Result<FlightBookingLookup, ProviderError>> RetrieveBookingAsync(ClientReference clientReference, CancellationToken cancellationToken) =>
        Task.FromResult(Result<FlightBookingLookup, ProviderError>.Failure(NotImplemented("booking lookup")));

    // Nothing is sent, so nothing can have happened: a definitive refusal, never an unknown outcome.
    private static ProviderError NotImplemented(string operation) =>
        new(ProviderErrorKind.InvalidRequest, $"Duffel {operation} is not implemented until sandbox verification and a supplier ADR (Q6).");

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = SupplierHttp.Bearer(options.Value.AccessToken!);
        request.Headers.Add("Duffel-Version", options.Value.ApiVersion);
        request.Headers.Accept.Add(new("application/json"));
        return request;
    }

    private async Task<Result<SupplierResponse, ProviderError>> Send(HttpRequestMessage request, TimeSpan timeout, string operation, CancellationToken cancellationToken)
    {
        var response = await SupplierHttp.SendAsync(httpClients.CreateClient(HttpClientName), request, timeout, SupplierCallKind.Read, cancellationToken);
        if (!response.IsSuccess)
        {
            LogFailure(logger, ProviderId, operation, response.Error.Kind);
        }

        return response;
    }

    // A response that does not match the documented shape is the supplier's unexpected answer: unavailable, logged.
    private Result<T, ProviderError> Map<T>(SupplierResponse response, Func<string, T> map)
        where T : notnull
    {
        try
        {
            return Result<T, ProviderError>.Success(map(response.Body));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            LogUnmappable(logger, ProviderId, exception.GetType().Name);
            return Result<T, ProviderError>.Failure(new ProviderError(ProviderErrorKind.Unavailable, "Unexpected Duffel response shape."));
        }
    }

    private static T Deserialize<T>(string body) =>
        (JsonSerializer.Deserialize<DuffelEnvelope<T>>(body, Json) ?? throw new JsonException("Empty Duffel response.")).Data;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flight provider {ProviderId} {Operation} failed: {ErrorKind}")]
    private static partial void LogFailure(ILogger logger, string providerId, string operation, ProviderErrorKind errorKind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flight provider {ProviderId} returned a response the adapter cannot map ({ExceptionType})")]
    private static partial void LogUnmappable(ILogger logger, string providerId, string exceptionType);
}
