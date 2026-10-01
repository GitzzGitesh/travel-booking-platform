using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks.Http;

namespace TravelBooking.Modules.Access.Infrastructure;

/// <summary>
/// Security events for refused staff requests (security rules: authorization failures are security events; F-61): a
/// signed-in staff member without the permission (403) is logged with our staff id, the route and the trace, never the
/// token. The response itself is the framework's usual 401 or 403.
/// </summary>
internal sealed partial class StaffAuthorizationEvents(ILogger<StaffAuthorizationEvents> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && policy.AuthenticationSchemes.Contains(StaffIdentity.Scheme))
        {
            LogForbidden(logger, context.User.StaffId() ?? "unknown", context.Request.Path.Value ?? string.Empty, context.TraceIdentifier);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    [LoggerMessage(Level = LogLevel.Warning, EventName = "StaffAuthorizationDenied", Message = "Security: staff member {StaffId} was refused {Path} (no permission; trace {TraceId})")]
    private static partial void LogForbidden(ILogger logger, string staffId, string path, string traceId);
}
