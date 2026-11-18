using System.Text.RegularExpressions;
using Serilog.Context;

namespace PHANTOM.API.Middleware;

/// <summary>Accepts a safe inbound X-Correlation-ID or generates one; echoes it and enriches logs.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var inbound = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeId().IsMatch(inbound) ? inbound : context.TraceIdentifier;

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$")]
    private static partial Regex SafeId();
}
