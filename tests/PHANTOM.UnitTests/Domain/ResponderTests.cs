using PHANTOM.Domain.Responders;

namespace PHANTOM.UnitTests.Domain;

/// <summary>Requirement 10.1, 10.2 and 10.4.</summary>
public sealed class ResponderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static Responder Make(string callSign = "med-07", params string[] skills) =>
        Responder.Create(Guid.NewGuid(), callSign, " Amina Bello ", "Medical Response", null, "Amina@Example.com", skills, null, Now).Value;

    [Theory]
    [InlineData("med-07", "MED-07")]
    [InlineData(" FIRE2 ", "FIRE2")]
    [InlineData("A1", "A1")]
    public void Call_signs_are_normalised(string input, string expected) => Assert.Equal(expected, Make(input).CallSign);

    [Theory]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("-MED")]
    [InlineData("MED 07")]
    [InlineData("MED_07")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void Invalid_call_signs_are_rejected(string input) =>
        Assert.True(Responder.Create(Guid.NewGuid(), input, "Name", "Team", null, null, [], null, Now).IsFailure);

    [Fact]
    public void Details_are_trimmed_and_skills_lower_cased_and_deduplicated()
    {
        var responder = Make("med-07", "First Aid", "first aid ", "  ", "Paramedic");

        Assert.Equal("Amina Bello", responder.FullName);
        Assert.Equal("amina@example.com", responder.Email);
        Assert.Equal(["first aid", "paramedic"], responder.Skills);
        Assert.Equal(ResponderAvailability.Available, responder.Availability);
        Assert.True(responder.IsActive);
    }

    [Fact]
    public void Assigned_is_never_set_by_hand()
    {
        var responder = Make();
        Assert.True(responder.SetAvailability(ResponderAvailability.Assigned, hasActiveAssignment: false, Now).IsFailure);
        Assert.Equal(ResponderAvailability.Available, responder.Availability);
    }

    [Fact]
    public void Availability_cannot_change_during_an_active_assignment_or_while_inactive()
    {
        var responder = Make();
        Assert.True(responder.SetAvailability(ResponderAvailability.Unavailable, hasActiveAssignment: true, Now).IsFailure);
        Assert.Equal(ResponderAvailability.Available, responder.Availability);

        Assert.True(responder.Deactivate(hasActiveAssignment: false, Now).IsSuccess);
        Assert.True(responder.SetAvailability(ResponderAvailability.Available, hasActiveAssignment: false, Now).IsFailure);
    }

    [Fact]
    public void Availability_change_records_the_time_and_bumps_the_version()
    {
        var responder = Make();
        var later = Now.AddMinutes(5);

        Assert.True(responder.SetAvailability(ResponderAvailability.Unavailable, hasActiveAssignment: false, later).IsSuccess);

        Assert.Equal(ResponderAvailability.Unavailable, responder.Availability);
        Assert.Equal(later, responder.LastStatusAt);
        Assert.Equal(1, responder.Version);
        Assert.True(responder.SetAvailability(ResponderAvailability.Unavailable, hasActiveAssignment: false, later).IsSuccess);
        Assert.Equal(1, responder.Version);
    }

    [Fact]
    public void Deactivation_requires_no_active_assignment_and_reactivation_starts_unavailable()
    {
        var responder = Make();
        Assert.True(responder.Deactivate(hasActiveAssignment: true, Now).IsFailure);
        Assert.True(responder.IsActive);

        Assert.True(responder.Deactivate(hasActiveAssignment: false, Now).IsSuccess);
        Assert.False(responder.IsActive);
        Assert.Equal(ResponderAvailability.Unavailable, responder.Availability);
        Assert.True(responder.Deactivate(hasActiveAssignment: false, Now).IsFailure);

        Assert.True(responder.Activate(Now).IsSuccess);
        Assert.True(responder.IsActive);
        Assert.Equal(ResponderAvailability.Unavailable, responder.Availability);
    }
}

public sealed class ResponderAssignmentEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assignment_moves_the_responder_to_assigned_and_release_returns_them_without_version_changes()
    {
        var responder = Responder.Create(Guid.NewGuid(), "MED-09", "Name", "Team", null, null, [], null, Now).Value;

        responder.MarkAssigned(Now);
        Assert.Equal(ResponderAvailability.Assigned, responder.Availability);
        responder.ReleaseFromAssignment(Now);
        Assert.Equal(ResponderAvailability.Available, responder.Availability);
        Assert.Equal(0, responder.Version);
    }

    [Fact]
    public void Each_status_change_bumps_the_assignment_version()
    {
        var assignment = PHANTOM.Domain.Assignments.ResponderAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.True(assignment.Acknowledge(Now).IsSuccess);
        Assert.True(assignment.MarkEnRoute(Now).IsSuccess);
        Assert.True(assignment.MarkEnRoute(Now).IsFailure);
        Assert.Equal(2, assignment.Version);
    }
}
