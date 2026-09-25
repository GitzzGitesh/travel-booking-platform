namespace TravelBooking.Api;

/// <summary>
/// Security headers for every response the application pipeline produces, including errors (.claude/rules/security.md).
/// The API serves JSON only, so its CSP allows nothing and forbids framing. HSTS is added separately outside
/// Development. Rejections that happen before the pipeline (framework host filtering, Kestrel protocol errors) have
/// empty bodies and don't carry these headers.
/// </summary>
internal static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // OnStarting runs even after the exception handler clears a failed response.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });

            await next(context);
        });
}
