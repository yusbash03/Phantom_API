using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace PHANTOM.API.Infrastructure;

public static class RateLimitPolicies
{
    public const string Public = "public";
    public const string Login = "login";

    /// <summary>Session refresh happens on every page load, so it has its own, larger budget.</summary>
    public const string Refresh = "refresh";

    public static void Configure(RateLimiterOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        var section = configuration.GetSection("RateLimiting");
        var publicPermits = section.GetValue("PublicPermitsPerMinute", 20);
        var loginPermits = section.GetValue("LoginPermitsPerMinute", 10);
        var refreshPermits = section.GetValue("RefreshPermitsPerMinute", 60);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Public, ctx => PerIp(ctx, publicPermits));
        options.AddPolicy(Login, ctx => PerIp(ctx, loginPermits));
        options.AddPolicy(Refresh, ctx => PerIp(ctx, refreshPermits));
    }

    // Partitioned by client IP as resolved by ForwardedHeaders (trusted proxies only).
    private static RateLimitPartition<string> PerIp(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
