namespace PHANTOM.Application.Realtime;

/// <summary>What changed. Requirement 13.1 lists the minimum; the extra types keep every screen current.</summary>
public enum RealtimeEventType
{
    IncidentCreated,
    IncidentUpdated,
    IncidentStatusChanged,
    IncidentResolved,
    AnalysisCompleted,
    AssignmentCreated,
    AssignmentAcknowledged,
    AssignmentStatusChanged,
    AssignmentUpdated,
    ResponderAvailabilityChanged,
    NotificationCreated,
}

/// <summary>
/// The payload sent to clients (Requirement 13.4): identifiers and a timestamp only. Clients refetch through
/// the authorised API rather than trusting event data (13.2).
/// </summary>
public sealed record RealtimeEvent(RealtimeEventType Type, Guid? IncidentId, Guid? AssignmentId, Guid? ResponderId, DateTimeOffset At);

/// <summary>An event and, for personal events such as notifications, the only user who may receive it.</summary>
public sealed record RealtimeDelivery(RealtimeEvent Event, Guid? RecipientUserId = null);

/// <summary>Delivers committed changes to connected clients. Must never throw into the caller.</summary>
public interface IRealtimePublisher
{
    Task PublishAsync(IReadOnlyList<RealtimeDelivery> deliveries, CancellationToken cancellationToken);
}

/// <summary>Used where no real-time transport is hosted (tooling, some tests).</summary>
public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task PublishAsync(IReadOnlyList<RealtimeDelivery> deliveries, CancellationToken cancellationToken) => Task.CompletedTask;
}
