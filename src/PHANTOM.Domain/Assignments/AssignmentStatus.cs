namespace PHANTOM.Domain.Assignments;

public enum AssignmentStatus
{
    Assigned,
    Acknowledged,
    EnRoute,
    OnScene,
    Completed,
    Declined,
    Cancelled,
}

/// <summary>Assignment transitions (requirements.md 11.3). Anything not listed is forbidden.</summary>
public static class AssignmentStatusPolicy
{
    private static readonly HashSet<(AssignmentStatus From, AssignmentStatus To)> Allowed =
    [
        (AssignmentStatus.Assigned, AssignmentStatus.Acknowledged),
        (AssignmentStatus.Acknowledged, AssignmentStatus.EnRoute),
        (AssignmentStatus.EnRoute, AssignmentStatus.OnScene),
        (AssignmentStatus.OnScene, AssignmentStatus.Completed),
        (AssignmentStatus.Assigned, AssignmentStatus.Declined),
        (AssignmentStatus.Acknowledged, AssignmentStatus.Declined),
        (AssignmentStatus.Assigned, AssignmentStatus.Cancelled),
        (AssignmentStatus.Acknowledged, AssignmentStatus.Cancelled),
        (AssignmentStatus.EnRoute, AssignmentStatus.Cancelled),
        (AssignmentStatus.OnScene, AssignmentStatus.Cancelled),
    ];

    public static IReadOnlyCollection<(AssignmentStatus From, AssignmentStatus To)> AllowedTransitions => Allowed;

    public static bool CanTransition(AssignmentStatus from, AssignmentStatus to) => Allowed.Contains((from, to));

    public static bool IsTerminal(AssignmentStatus status) =>
        status is AssignmentStatus.Completed or AssignmentStatus.Declined or AssignmentStatus.Cancelled;
}
