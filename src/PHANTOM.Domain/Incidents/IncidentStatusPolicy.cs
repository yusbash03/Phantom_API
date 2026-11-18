namespace PHANTOM.Domain.Incidents;

/// <summary>The named operations that can move an incident between statuses.</summary>
public enum IncidentTransition
{
    BeginReview,
    Verify,
    Reject,
    CloseAsDuplicate,
    FirstAssignmentCreated,
    ResponderEnRoute,
    AllAssignmentsEnded,
    Resolve,
    Reopen,
    Close,
}

public enum TransitionActor
{
    Coordinator,
    System,
}

/// <summary>
/// The single source of truth for incident status transitions (requirements.md 5.3).
/// Anything not in <see cref="Rules"/> is forbidden.
/// </summary>
public static class IncidentStatusPolicy
{
    public sealed record Rule(IncidentStatus From, IncidentTransition Transition, IncidentStatus To, TransitionActor Actor);

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new(IncidentStatus.Reported, IncidentTransition.BeginReview, IncidentStatus.UnderReview, TransitionActor.Coordinator),
        new(IncidentStatus.Reported, IncidentTransition.Reject, IncidentStatus.Rejected, TransitionActor.Coordinator),
        new(IncidentStatus.Reported, IncidentTransition.CloseAsDuplicate, IncidentStatus.Closed, TransitionActor.Coordinator),
        new(IncidentStatus.UnderReview, IncidentTransition.Verify, IncidentStatus.Verified, TransitionActor.Coordinator),
        new(IncidentStatus.UnderReview, IncidentTransition.Reject, IncidentStatus.Rejected, TransitionActor.Coordinator),
        new(IncidentStatus.UnderReview, IncidentTransition.CloseAsDuplicate, IncidentStatus.Closed, TransitionActor.Coordinator),
        new(IncidentStatus.Verified, IncidentTransition.FirstAssignmentCreated, IncidentStatus.Assigned, TransitionActor.System),
        new(IncidentStatus.Verified, IncidentTransition.Resolve, IncidentStatus.Resolved, TransitionActor.Coordinator),
        new(IncidentStatus.Assigned, IncidentTransition.ResponderEnRoute, IncidentStatus.InProgress, TransitionActor.System),
        new(IncidentStatus.Assigned, IncidentTransition.AllAssignmentsEnded, IncidentStatus.Verified, TransitionActor.System),
        new(IncidentStatus.Assigned, IncidentTransition.Resolve, IncidentStatus.Resolved, TransitionActor.Coordinator),
        new(IncidentStatus.InProgress, IncidentTransition.Resolve, IncidentStatus.Resolved, TransitionActor.Coordinator),
        new(IncidentStatus.Resolved, IncidentTransition.Close, IncidentStatus.Closed, TransitionActor.Coordinator),
        new(IncidentStatus.Resolved, IncidentTransition.Reopen, IncidentStatus.InProgress, TransitionActor.Coordinator),
    ];

    private static readonly Dictionary<(IncidentStatus, IncidentTransition), Rule> Index =
        Rules.ToDictionary(r => (r.From, r.Transition));

    public static bool TryGetRule(IncidentStatus from, IncidentTransition transition, out Rule? rule) =>
        Index.TryGetValue((from, transition), out rule);

    public static bool IsTerminal(IncidentStatus status) => status is IncidentStatus.Closed or IncidentStatus.Rejected;

    public static bool IsActive(IncidentStatus status) =>
        status is not (IncidentStatus.Resolved or IncidentStatus.Closed or IncidentStatus.Rejected);
}
