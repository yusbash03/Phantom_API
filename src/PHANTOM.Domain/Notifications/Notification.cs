namespace PHANTOM.Domain.Notifications;

/// <summary>What happened (Requirement 14.1). Values are persisted; never rename.</summary>
public enum NotificationType
{
    AssignmentCreated,
    AssignmentCancelled,
    AssignmentDeclined,
    AssignmentCompleted,
    CriticalAiRecommendation,
    PossibleDuplicate,
}

/// <summary>An in-app message for one user. Only its read state ever changes.</summary>
public sealed class Notification
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 1000;

    private Notification()
    {
        // EF Core
        Title = string.Empty;
        Body = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public NotificationType Type { get; private set; }
    public string Title { get; private set; }
    public string Body { get; private set; }
    public Guid? IncidentId { get; private set; }
    public Guid? AssignmentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static Notification Create(Guid id, Guid recipientUserId, NotificationType type, string title, string body,
        Guid? incidentId, Guid? assignmentId, DateTimeOffset now)
    {
        if (recipientUserId == Guid.Empty)
        {
            throw new ArgumentException("A notification needs a recipient.", nameof(recipientUserId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new Notification
        {
            Id = id,
            RecipientUserId = recipientUserId,
            Type = type,
            Title = Bound(title.Trim(), MaxTitleLength),
            Body = Bound(body?.Trim() ?? string.Empty, MaxBodyLength),
            IncidentId = incidentId,
            AssignmentId = assignmentId,
            CreatedAt = now,
        };
    }

    /// <summary>Idempotent: the first read time is kept.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
