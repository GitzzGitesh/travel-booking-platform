using System.Net;
using System.Net.Http.Headers;

namespace TravelBooking.BuildingBlocks.Providers.Http;

/// <summary>
/// Configuration every HTTP supplier adapter shares (provider-integration.md, Timeouts and resilience). Adapter options
/// derive from it and add their credentials. Nothing here has a secret default: base URLs and credentials come from
/// configuration and user-secrets / Key Vault (security rules).
/// </summary>
public abstract class SupplierHttpOptions
{
    /// <summary>Off unless explicitly enabled: an adapter without verified access is never composed by accident.</summary>
    public bool Enabled { get; set; }

    /// <summary>The supplier's API base address (sandbox or production), from configuration.</summary>
    public Uri? BaseUrl { get; set; }

    public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Revalidation and booking lookup: idempotent reads.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Booking, ticketing and cancellation: writes, never retried.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The shared checks; adapters add their credential checks.</summary>
    public virtual IEnumerable<string> Problems()
    {
        if (BaseUrl is not { IsAbsoluteUri: true } || BaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            yield return "BaseUrl must be an absolute https URL.";
        }

        foreach (var (name, timeout) in new[] { ("SearchTimeout", SearchTimeout), ("ReadTimeout", ReadTimeout), ("WriteTimeout", WriteTimeout) })
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            {
                yield return $"{name} must be between 0 and 5 minutes.";
            }
        }
    }
}

/// <summary>Whether a supplier call is a read (safe to repeat) or a write (never repeated blindly; booking rules).</summary>
public enum SupplierCallKind
{
    Read,
    Write,
}

/// <summary>A supplier response the adapter maps; an error is already classified in the shared taxonomy.</summary>
public sealed record SupplierResponse(HttpStatusCode Status, string Body);

/// <summary>
/// Sends one supplier request with its operation's timeout and classifies failures into <see cref="ProviderErrorKind"/>
/// (supplier codes never leave the adapter). No retry here: retries are added only for reads, through the resilience
/// pipeline hook (ADR 0003: Microsoft.Extensions.Http.Resilience), never for writes.
/// A timeout, a broken connection or an unexpected exception on a WRITE is <see cref="ProviderErrorKind.Unknown"/>: the
/// supplier may have acted, so the core reconciles, never resubmits. On a read it is <see cref="ProviderErrorKind.Unavailable"/>.
/// </summary>
public static class SupplierHttp
{
    /// <param name="describeFailure">
    /// Optional: a diagnostic suffix for an error response, from its body (e.g. the supplier's error code), or null. It
    /// only adds to the message: the kind always comes from <see cref="Classify"/>, so a write's Unknown stays Unknown.
    /// </param>
    public static async Task<Result<SupplierResponse, ProviderError>> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan timeout,
        SupplierCallKind kind,
        CancellationToken cancellationToken,
        Func<SupplierResponse, string?>? describeFailure = null)
    {
        // Cancelled by the caller before anything is sent: nothing was attempted.
        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            if (response.IsSuccessStatusCode)
            {
                return Result<SupplierResponse, ProviderError>.Success(new SupplierResponse(response.StatusCode, body));
            }

            var error = new ProviderError(Classify(response.StatusCode, kind), $"HTTP {(int)response.StatusCode}");
            return Result<SupplierResponse, ProviderError>.Failure(describeFailure?.Invoke(new SupplierResponse(response.StatusCode, body)) is { Length: > 0 } suffix
                ? error with { Message = $"{error.Message}, {suffix}" }
                : error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(kind, "timed out");
        }
        catch (HttpRequestException exception)
        {
            return Failure(kind, $"connection failed ({exception.HttpRequestError})");
        }
    }

    /// <summary>
    /// The shared status mapping. A write's 5xx is <see cref="ProviderErrorKind.Unknown"/> (processing may have
    /// happened). Adapters refine what the status alone cannot tell (e.g. a supplier's own "offer expired" code).
    /// </summary>
    public static ProviderErrorKind Classify(HttpStatusCode status, SupplierCallKind kind) => (int)status switch
    {
        400 or 404 or 422 => ProviderErrorKind.InvalidRequest,
        401 or 403 => ProviderErrorKind.AuthFailure,
        409 => ProviderErrorKind.IdempotencyConflict,
        429 => ProviderErrorKind.RateLimited,
        >= 500 when kind == SupplierCallKind.Write => ProviderErrorKind.Unknown,
        >= 500 => ProviderErrorKind.Unavailable,
        _ => kind == SupplierCallKind.Write ? ProviderErrorKind.Unknown : ProviderErrorKind.Unavailable,
    };

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static Result<SupplierResponse, ProviderError> Failure(SupplierCallKind kind, string reason) =>
        Result<SupplierResponse, ProviderError>.Failure(new ProviderError(kind == SupplierCallKind.Write ? ProviderErrorKind.Unknown : ProviderErrorKind.Unavailable, reason));
}

/// <summary>An access token and when it stops being usable.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Caches a supplier access token (OAuth client credentials and similar) and refreshes it shortly before it expires, one
/// refresh at a time. The token is never logged. How a token is obtained is the adapter's: suppliers differ.
/// </summary>
public sealed class AccessTokenCache(Func<CancellationToken, Task<Result<AccessToken, ProviderError>>> acquire, TimeProvider timeProvider)
{
    private static readonly TimeSpan _refreshMargin = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private AccessToken? _current;

    public async Task<Result<string, ProviderError>> GetAsync(CancellationToken cancellationToken)
    {
        if (Usable(_current) is { } token)
        {
            return Result<string, ProviderError>.Success(token);
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (Usable(_current) is { } refreshed)
            {
                return Result<string, ProviderError>.Success(refreshed);
            }

            var acquired = await acquire(cancellationToken);
            if (!acquired.IsSuccess)
            {
                return Result<string, ProviderError>.Failure(acquired.Error);
            }

            _current = acquired.Value;
            return Result<string, ProviderError>.Success(acquired.Value.Value);
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Forgets the token, after the supplier rejected it (401): the next call acquires a new one.</summary>
    public void Invalidate() => _current = null;

    private string? Usable(AccessToken? token) =>
        token is not null && token.ExpiresAt - _refreshMargin > timeProvider.GetUtcNow() ? token.Value : null;
}
