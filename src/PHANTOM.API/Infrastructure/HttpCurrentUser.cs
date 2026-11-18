using PHANTOM.API.Middleware;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure.Identity;

namespace PHANTOM.API.Infrastructure;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst(PhantomClaimTypes.Subject)?.Value, out var id) ? id : null;

    public bool IsAuthenticated => accessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public bool IsInRole(string role) => accessor.HttpContext?.User.IsInRole(role) == true;

    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] as string;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
