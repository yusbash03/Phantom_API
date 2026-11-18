using FsCheck.Xunit;
using PHANTOM.Domain.Assignments;

namespace PHANTOM.UnitTests.Domain;

public sealed class AssignmentTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    // From requirements.md 11.3, independent of the policy implementation.
    private static readonly HashSet<(AssignmentStatus, AssignmentStatus)> Required =
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

    [Property(MaxTest = 300)]
    public bool Policy_matches_requirements(AssignmentStatus from, AssignmentStatus to) =>
        AssignmentStatusPolicy.CanTransition(from, to) == Required.Contains((from, to));

    [Property(MaxTest = 300)]
    public bool Entity_moves_iff_policy_allows(AssignmentStatus from, AssignmentStatus to)
    {
        var assignment = At(from);
        var result = Move(assignment, to);
        var allowed = AssignmentStatusPolicy.CanTransition(from, to);
        return allowed ? result.IsSuccess && assignment.Status == to : result.IsFailure && assignment.Status == from;
    }

    [Fact]
    public void Terminal_assignments_are_inactive()
    {
        Assert.False(At(AssignmentStatus.Completed).IsActive);
        Assert.False(At(AssignmentStatus.Declined).IsActive);
        Assert.False(At(AssignmentStatus.Cancelled).IsActive);
        Assert.True(At(AssignmentStatus.OnScene).IsActive);
    }

    [Fact]
    public void Decline_requires_reason()
    {
        var assignment = At(AssignmentStatus.Assigned);
        Assert.True(assignment.Decline("", Now).IsFailure);
        Assert.Equal(AssignmentStatus.Assigned, assignment.Status);
    }

    private static ResponderAssignment At(AssignmentStatus status)
    {
        var a = ResponderAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        AssignmentStatus[] path = status switch
        {
            AssignmentStatus.Assigned => [],
            AssignmentStatus.Acknowledged => [AssignmentStatus.Acknowledged],
            AssignmentStatus.EnRoute => [AssignmentStatus.Acknowledged, AssignmentStatus.EnRoute],
            AssignmentStatus.OnScene => [AssignmentStatus.Acknowledged, AssignmentStatus.EnRoute, AssignmentStatus.OnScene],
            AssignmentStatus.Completed => [AssignmentStatus.Acknowledged, AssignmentStatus.EnRoute, AssignmentStatus.OnScene, AssignmentStatus.Completed],
            AssignmentStatus.Declined => [AssignmentStatus.Declined],
            AssignmentStatus.Cancelled => [AssignmentStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var step in path)
        {
            Assert.True(Move(a, step).IsSuccess);
        }

        return a;
    }

    private static PHANTOM.Domain.Common.Result Move(ResponderAssignment a, AssignmentStatus to) => to switch
    {
        AssignmentStatus.Acknowledged => a.Acknowledge(Now),
        AssignmentStatus.EnRoute => a.MarkEnRoute(Now),
        AssignmentStatus.OnScene => a.MarkOnScene(Now),
        AssignmentStatus.Completed => a.Complete("Scene cleared", Now),
        AssignmentStatus.Declined => a.Decline("Off shift", Now),
        AssignmentStatus.Cancelled => a.Cancel("Reassigned", Now),
        // There is no operation that returns an assignment to Assigned.
        AssignmentStatus.Assigned => PHANTOM.Domain.Common.Result.Failure(PHANTOM.Domain.Common.Error.InvalidTransition("none", "none")),
        _ => throw new ArgumentOutOfRangeException(nameof(to)),
    };
}
