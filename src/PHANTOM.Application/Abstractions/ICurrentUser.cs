namespace PHANTOM.Application.Abstractions;

/// <summary>The caller of the current request (or the system, for background work).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
    string? CorrelationId { get; }
    string? IpAddress { get; }
}
