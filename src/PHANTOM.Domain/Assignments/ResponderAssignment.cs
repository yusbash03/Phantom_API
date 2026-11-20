using PHANTOM.Domain.Common;

namespace PHANTOM.Domain.Assignments;

/// <summary>A coordinator's assignment of one responder to one incident.</summary>
public sealed class ResponderAssignment
{
    public const int MaxReasonLength = 1000;
    public const int MaxNotesLength = 5000;

    private ResponderAssignment()
    {
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ResponderId { get; private set; }
    public AssignmentStatus Status { get; private set; }
    public Guid AssignedById { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public DateTimeOffset? EnRouteAt { get; private set; }
    public DateTimeOffset? OnSceneAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? EndedReason { get; private set; }
    public string? OverrideReason { get; private set; }
    public string? CompletionNotes { get; private set; }
    /// <summary>Optimistic-concurrency version, incremented on every status change.</summary>
    public int Version { get; private set; }

    public bool IsActive => !AssignmentStatusPolicy.IsTerminal(Status);

    /// <summary>
    /// Only an identified coordinator creates assignments (Requirement 11.1); there is no system or AI path
    /// (11.7), so an empty <paramref name="assignedById"/> is refused.
    /// </summary>
    public static ResponderAssignment Create(Guid id, Guid incidentId, Guid responderId, Guid assignedById, DateTimeOffset now, string? overrideReason = null)
    {
        if (assignedById == Guid.Empty)
        {
            throw new ArgumentException("An assignment must be made by an identified coordinator.", nameof(assignedById));
        }

        return new()
        {
            Id = id,
            IncidentId = incidentId,
            ResponderId = responderId,
            AssignedById = assignedById,
            AssignedAt = now,
            Status = AssignmentStatus.Assigned,
            OverrideReason = string.IsNullOrWhiteSpace(overrideReason) ? null : overrideReason.Trim(),
        };
    }

    /// <summary>Acknowledgement is acceptance (design decision D6).</summary>
    public Result Acknowledge(DateTimeOffset now) => Move(AssignmentStatus.Acknowledged, () => AcknowledgedAt = now);

    public Result MarkEnRoute(DateTimeOffset now) => Move(AssignmentStatus.EnRoute, () => EnRouteAt = now);

    public Result MarkOnScene(DateTimeOffset now) => Move(AssignmentStatus.OnScene, () => OnSceneAt = now);

    public Result Complete(string notes, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(notes) || notes.Trim().Length > MaxNotesLength)
        {
            return Error.Validation("assignment.notes_required", $"Completion notes are required (at most {MaxNotesLength} characters).");
        }

        return Move(AssignmentStatus.Completed, () =>
        {
            CompletedAt = now;
            EndedAt = now;
            CompletionNotes = notes.Trim();
        });
    }

    public Result Decline(string reason, DateTimeOffset now) => End(AssignmentStatus.Declined, reason, now);

    public Result Cancel(string reason, DateTimeOffset now) => End(AssignmentStatus.Cancelled, reason, now);

    private Result End(AssignmentStatus target, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
        {
            return Error.Validation("assignment.reason_required", $"A reason is required (at most {MaxReasonLength} characters).");
        }

        return Move(target, () =>
        {
            EndedAt = now;
            EndedReason = reason.Trim();
        });
    }

    private Result Move(AssignmentStatus target, Action onSuccess)
    {
        if (!AssignmentStatusPolicy.CanTransition(Status, target))
        {
            return Error.InvalidTransition("assignment.invalid_transition", $"Cannot move an assignment from {Status} to {target}.");
        }

        Status = target;
        Version++;
        onSuccess();
        return Result.Success();
    }
}
