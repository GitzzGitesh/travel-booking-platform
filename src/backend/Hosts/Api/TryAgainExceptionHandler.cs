using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TravelBooking.BuildingBlocks;

namespace TravelBooking.Api;

/// <summary>
/// A request that changed nothing and may simply be repeated (<see cref="TryAgainException"/>) answers 503 with the same
/// stable Problem Details type checkout uses for this condition (<c>try-again</c>) and a short Retry-After; never the
/// exception's details. Everything else stays with the default handler (a generic 500).
/// </summary>
internal sealed class TryAgainExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not TryAgainException)
        {
            return false;
        }

        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        http.Response.Headers.RetryAfter = "1";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "try-again",
                Title = "We could not complete this right now. Nothing was changed. Please try again.",
            },
        });
    }
}
