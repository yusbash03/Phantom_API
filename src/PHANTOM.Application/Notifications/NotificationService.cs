using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Common;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Notifications;

namespace PHANTOM.Application.Notifications;

public sealed record NotificationListQuery(bool UnreadOnly = false, int Page = 1, int PageSize = 20);

public sealed record NotificationResponse(
    Guid Id, NotificationType Type, string Title, string Body, Guid? IncidentId, Guid? AssignmentId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int Page, int PageSize, int TotalCount, int UnreadCount);

public sealed record MarkAllReadResponse(int Updated);

public interface INotificationService
{
    Task<NotificationListResponse> ListAsync(NotificationListQuery query, CancellationToken cancellationToken);
    Task<Result> MarkReadAsync(Guid id, CancellationToken cancellationToken);
    Task<MarkAllReadResponse> MarkAllReadAsync(CancellationToken cancellationToken);
}

/// <summary>A user's own notifications only (Requirement 14.2, 14.3): others' are indistinguishable from missing.</summary>
internal sealed class NotificationService(IPhantomDbContext db, ICurrentUser currentUser, IClock clock) : INotificationService
{
    public async Task<NotificationListResponse> ListAsync(NotificationListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var mine = Mine();
        var unread = await mine.CountAsync(n => n.ReadAt == null, cancellationToken);
        var listed = query.UnreadOnly ? mine.Where(n => n.ReadAt == null) : mine;
        var total = query.UnreadOnly ? unread : await listed.CountAsync(cancellationToken);
        var items = await listed
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Select(n => new NotificationResponse(n.Id, n.Type, n.Title, n.Body, n.IncidentId, n.AssignmentId, n.CreatedAt, n.ReadAt))
            .ToListAsync(cancellationToken);
        return new NotificationListResponse(items, page.SafePage, page.SafePageSize, total, unread);
    }

    public async Task<Result> MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.RecipientUserId == currentUser.UserId, cancellationToken);
        if (notification is null)
        {
            return Error.NotFound("notification.not_found", "Notification not found.");
        }

        notification.MarkRead(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<MarkAllReadResponse> MarkAllReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var updated = await Mine().Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), cancellationToken);
        return new MarkAllReadResponse(updated);
    }

    private IQueryable<Notification> Mine()
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        return db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == userId);
    }
}
