namespace PHANTOM.Domain.Audit;

public enum AuditActorType
{
    User,
    System,
    Anonymous,
}

/// <summary>Append-only audit record. The database rejects UPDATE and DELETE on this table.</summary>
public sealed class AuditLog
{
    private AuditLog()
    {
        Action = string.Empty;
        EntityType = string.Empty;
    }

    public long Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public AuditActorType ActorType { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; }
    public string EntityType { get; private set; }
    public string? EntityId { get; private set; }
    public string? PreviousValue { get; private set; }
    public string? NewValue { get; private set; }
    public string? Metadata { get; private set; }
    public string? CorrelationId { get; private set; }

    public static AuditLog Create(
        DateTimeOffset occurredAt,
        AuditActorType actorType,
        Guid? actorUserId,
        string action,
        string entityType,
        string? entityId,
        string? previousValueJson,
        string? newValueJson,
        string? metadataJson,
        string? correlationId) =>
        new()
        {
            OccurredAt = occurredAt,
            ActorType = actorType,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            PreviousValue = previousValueJson,
            NewValue = newValueJson,
            Metadata = metadataJson,
            CorrelationId = correlationId,
        };
}
