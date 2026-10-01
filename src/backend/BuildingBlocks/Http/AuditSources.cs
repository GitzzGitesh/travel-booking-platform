using Microsoft.AspNetCore.Http;
using TravelBooking.BuildingBlocks.Audit;

namespace TravelBooking.BuildingBlocks.Http;

public static class AuditSources
{
    /// <summary>The audit source of this request: the W3C trace id, the client address and the user agent (clipped).</summary>
    public static AuditSource From(HttpContext http) => new(
        System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
        http.Connection.RemoteIpAddress?.ToString(),
        AuditEntry.Clip(http.Request.Headers.UserAgent.ToString(), 256));
}
