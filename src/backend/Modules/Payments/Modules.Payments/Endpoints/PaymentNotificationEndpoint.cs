using Microsoft.AspNetCore.Http;
using TravelBooking.Modules.Payments.Application;

namespace TravelBooking.Modules.Payments.Endpoints;

/// <summary>
/// A payment provider's notification (webhook). The provider's signature over the raw body authenticates it, not a
/// user, so it is anonymous by design. Thin: read the raw body, hand it to the handler, answer fast (booking rules).
/// </summary>
internal static class PaymentNotificationEndpoint
{
    /// <summary>Provider events are small; anything larger is refused.</summary>
    public const int MaxBodyChars = 64 * 1024;

    public static async Task<IResult> Handle(string providerId, HttpRequest request, ReceivePaymentNotificationHandler handler, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxBodyChars)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // The exact text the provider signed: never re-serialised.
        using var reader = new StreamReader(request.Body);
        var buffer = new char[MaxBodyChars + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (length > MaxBodyChars)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return await handler.HandleAsync(providerId, new string(buffer, 0, length), headers, cancellationToken) switch
        {
            PaymentNotificationReceipt.UnknownProvider => Results.NotFound(),
            PaymentNotificationReceipt.Rejected => Results.BadRequest(),
            _ => Results.Ok(),
        };
    }
}
