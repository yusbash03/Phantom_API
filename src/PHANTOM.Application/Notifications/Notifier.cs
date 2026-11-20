using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Notifications;

namespace PHANTOM.Application.Notifications;

/// <summary>
/// Adds notifications to the current unit of work, so each one is committed (or rolled back) together
/// with the change it reports (Requirement 14.1).
/// </summary>
public interface INotifier
{
    void NotifyUser(Guid userId, NotificationType type, string title, string body, Guid? incidentId, Guid? assignmentId);

    /// <summary>Every active Incident Coordinator.</summary>
    Task NotifyCoordinatorsAsync(NotificationType type, string title, string body, Guid? incidentId, Guid? assignmentId, CancellationToken cancellationToken);
}

internal sealed class Notifier(IPhantomDbContext db, IClock clock) : INotifier
{
    public void NotifyUser(Guid userId, NotificationType type, string title, string body, Guid? incidentId, Guid? assignmentId) =>
        db.Notifications.Add(Notification.Create(Guid.CreateVersion7(), userId, type, title, body, incidentId, assignmentId, clock.UtcNow));

    public async Task NotifyCoordinatorsAsync(NotificationType type, string title, string body, Guid? incidentId, Guid? assignmentId, CancellationToken cancellationToken)
    {
        var coordinators = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Roles.Any(r => r.RoleId == Role.IncidentCoordinatorId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        foreach (var userId in coordinators)
        {
            NotifyUser(userId, type, title, body, incidentId, assignmentId);
        }
    }
}
