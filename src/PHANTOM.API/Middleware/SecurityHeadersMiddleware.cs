namespace PHANTOM.API.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (!context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        }

        return next(context);
    }
}
