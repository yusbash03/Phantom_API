using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Domain;

/// <summary>Drives an incident into any status using only legitimate transitions.</summary>
internal static class IncidentTestBuilder
{
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid Coordinator = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Incident New()
    {
        var location = Location.Reported("Corner of Main St and 5th Ave", null, null).Value;
        return Incident.Create(Guid.NewGuid(), "PH-2026-000001", "Smoke from warehouse", "Thick smoke seen.", IncidentCategory.Fire, location, Now).Value;
    }

    public static Incident At(IncidentStatus status)
    {
        var incident = New();
        var path = status switch
        {
            IncidentStatus.Reported => [],
            IncidentStatus.UnderReview => [IncidentTransition.BeginReview],
            IncidentStatus.Verified => [IncidentTransition.BeginReview, IncidentTransition.Verify],
            IncidentStatus.Assigned => [IncidentTransition.BeginReview, IncidentTransition.Verify, IncidentTransition.FirstAssignmentCreated],
            IncidentStatus.InProgress => [IncidentTransition.BeginReview, IncidentTransition.Verify, IncidentTransition.FirstAssignmentCreated, IncidentTransition.ResponderEnRoute],
            IncidentStatus.Resolved => [IncidentTransition.BeginReview, IncidentTransition.Verify, IncidentTransition.Resolve],
            IncidentStatus.Closed => [IncidentTransition.BeginReview, IncidentTransition.Verify, IncidentTransition.Resolve, IncidentTransition.Close],
            IncidentStatus.Rejected => new[] { IncidentTransition.Reject },
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        foreach (var transition in path)
        {
            var result = Apply(incident, transition);
            Assert.True(result.IsSuccess, $"Setup failed applying {transition}: {result.Error?.Message}");
        }

        Assert.Equal(status, incident.Status);
        return incident;
    }

    /// <summary>Invokes the domain method for a transition with valid arguments.</summary>
    public static PHANTOM.Domain.Common.Result Apply(Incident incident, IncidentTransition transition) => transition switch
    {
        IncidentTransition.BeginReview => incident.BeginReview(Coordinator, Now),
        IncidentTransition.Verify => incident.Verify(Severity.High, Coordinator, Now),
        IncidentTransition.Reject => incident.Reject("Hoax report", Now),
        IncidentTransition.CloseAsDuplicate => incident.CloseAsDuplicate("PH-2026-000099", Now),
        IncidentTransition.FirstAssignmentCreated => incident.MarkAssigned(Now),
        IncidentTransition.ResponderEnRoute => incident.MarkInProgress(Now),
        IncidentTransition.AllAssignmentsEnded => incident.RevertToVerified(Now),
        IncidentTransition.Resolve => incident.Resolve("Fire extinguished", Coordinator, Now),
        IncidentTransition.Reopen => incident.Reopen("Flare-up reported", Now),
        IncidentTransition.Close => incident.Close(Now),
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };
}
