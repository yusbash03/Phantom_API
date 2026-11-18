using System.Text.Json;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Audit;

namespace PHANTOM.Application.Audit;

internal sealed class AuditWriter(IPhantomDbContext db, ICurrentUser currentUser, IClock clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(string action, string entityType, string? entityId, object? previousValue = null, object? newValue = null, object? metadata = null)
    {
        var actorType = currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.Anonymous;
        Add(actorType, currentUser.UserId, action, entityType, entityId, previousValue, newValue, metadata);
    }

    public void RecordSystem(string action, string entityType, string? entityId, object? previousValue = null, object? newValue = null, object? metadata = null) =>
        Add(AuditActorType.System, null, action, entityType, entityId, previousValue, newValue, metadata);

    private void Add(AuditActorType actorType, Guid? actorUserId, string action, string entityType, string? entityId, object? previousValue, object? newValue, object? metadata) =>
        db.AuditLogs.Add(AuditLog.Create(
            clock.UtcNow,
            actorType,
            actorUserId,
            action,
            entityType,
            entityId,
            Serialize(previousValue),
            Serialize(newValue),
            Serialize(metadata),
            currentUser.CorrelationId));

    private static string? Serialize(object? value) => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
