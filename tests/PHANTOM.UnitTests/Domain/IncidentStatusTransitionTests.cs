using FsCheck.Xunit;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Domain;

/// <summary>Critical property 6: incident status transitions follow requirements.md 5.3 exactly.</summary>
public sealed class IncidentStatusTransitionTests
{
    // Written independently from IncidentStatusPolicy, straight from the requirements table.
    private static readonly HashSet<(IncidentStatus From, IncidentStatus To)> RequiredPairs =
    [
        (IncidentStatus.Reported, IncidentStatus.UnderReview),
        (IncidentStatus.Reported, IncidentStatus.Rejected),
        (IncidentStatus.Reported, IncidentStatus.Closed),
        (IncidentStatus.UnderReview, IncidentStatus.Verified),
        (IncidentStatus.UnderReview, IncidentStatus.Rejected),
        (IncidentStatus.UnderReview, IncidentStatus.Closed),
        (IncidentStatus.Verified, IncidentStatus.Assigned),
        (IncidentStatus.Verified, IncidentStatus.Resolved),
        (IncidentStatus.Assigned, IncidentStatus.InProgress),
        (IncidentStatus.Assigned, IncidentStatus.Verified),
        (IncidentStatus.Assigned, IncidentStatus.Resolved),
        (IncidentStatus.InProgress, IncidentStatus.Resolved),
        (IncidentStatus.Resolved, IncidentStatus.Closed),
        (IncidentStatus.Resolved, IncidentStatus.InProgress),
    ];

    [Fact]
    public void Policy_allows_exactly_the_required_status_pairs()
    {
        var policyPairs = IncidentStatusPolicy.Rules.Select(r => (r.From, r.To)).ToHashSet();
        Assert.Equal(RequiredPairs.OrderBy(p => p.ToString()), policyPairs.OrderBy(p => p.ToString()));
    }

    [Property(MaxTest = 500)]
    public bool Transition_succeeds_iff_policy_has_a_rule(IncidentStatus from, IncidentTransition transition)
    {
        var incident = IncidentTestBuilder.At(from);
        var hasRule = IncidentStatusPolicy.TryGetRule(from, transition, out var rule);
        var result = IncidentTestBuilder.Apply(incident, transition);

        // MarkInProgress is idempotent on InProgress (a second responder going en route is not an error).
        if (from == IncidentStatus.InProgress && transition == IncidentTransition.ResponderEnRoute)
        {
            return result.IsSuccess && incident.Status == IncidentStatus.InProgress;
        }

        return hasRule
            ? result.IsSuccess && incident.Status == rule!.To
            : result.IsFailure && incident.Status == from;
    }

    [Theory]
    [InlineData(IncidentStatus.Closed)]
    [InlineData(IncidentStatus.Rejected)]
    public void Terminal_statuses_have_no_outgoing_transitions(IncidentStatus terminal)
    {
        Assert.True(IncidentStatusPolicy.IsTerminal(terminal));
        Assert.DoesNotContain(IncidentStatusPolicy.Rules, r => r.From == terminal);
    }

    [Fact]
    public void Verify_records_approved_severity_and_verifier()
    {
        var incident = IncidentTestBuilder.At(IncidentStatus.UnderReview);
        Assert.True(incident.Verify(Severity.Critical, IncidentTestBuilder.Coordinator, IncidentTestBuilder.Now).IsSuccess);
        Assert.Equal(Severity.Critical, incident.ApprovedSeverity);
        Assert.Equal(IncidentTestBuilder.Coordinator, incident.VerifiedById);
    }

    [Fact]
    public void Failed_verify_does_not_set_approved_severity()
    {
        var incident = IncidentTestBuilder.At(IncidentStatus.Reported);
        Assert.True(incident.Verify(Severity.Critical, IncidentTestBuilder.Coordinator, IncidentTestBuilder.Now).IsFailure);
        Assert.Null(incident.ApprovedSeverity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_requires_a_reason(string reason)
    {
        var incident = IncidentTestBuilder.At(IncidentStatus.Reported);
        Assert.True(incident.Reject(reason, IncidentTestBuilder.Now).IsFailure);
        Assert.Equal(IncidentStatus.Reported, incident.Status);
    }

    [Fact]
    public void Resolve_requires_resolution_details()
    {
        var incident = IncidentTestBuilder.At(IncidentStatus.Verified);
        Assert.True(incident.Resolve(" ", IncidentTestBuilder.Coordinator, IncidentTestBuilder.Now).IsFailure);
        Assert.Equal(IncidentStatus.Verified, incident.Status);
    }

    [Fact]
    public void Closing_as_duplicate_of_itself_is_rejected()
    {
        var incident = IncidentTestBuilder.At(IncidentStatus.Reported);
        Assert.True(incident.CloseAsDuplicate(incident.Number, IncidentTestBuilder.Now).IsFailure);
    }
}
