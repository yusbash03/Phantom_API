using System.Text.RegularExpressions;
using PHANTOM.Domain.Common;

namespace PHANTOM.Domain.Responders;

public enum ResponderAvailability
{
    Available,

    /// <summary>Derived from an active assignment; never set by hand (Requirement 10.4).</summary>
    Assigned,
    Unavailable,
}

/// <summary>A person coordinators can assign to incidents. Never deleted; deactivated instead.</summary>
public sealed partial class Responder
{
    public const int MaxCallSignLength = 20;
    public const int MaxNameLength = 200;
    public const int MaxTeamLength = 100;
    public const int MaxPhoneLength = 50;
    public const int MaxEmailLength = 320;
    public const int MaxSkillLength = 50;
    public const int MaxSkills = 20;

    private Responder()
    {
        // EF Core
        CallSign = string.Empty;
        FullName = string.Empty;
        Team = string.Empty;
        Skills = [];
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, upper-case identifier used on the radio and in assignments (e.g. "MED-07"). Fixed once created.</summary>
    public string CallSign { get; private set; }

    public string FullName { get; private set; }
    public string Team { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }

    /// <summary>Lower-case, de-duplicated so filtering by skill is reliable.</summary>
    public string[] Skills { get; private set; }

    public ResponderAvailability Availability { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Optional account the responder signs in with; required for them to see their own assignments.</summary>
    public Guid? UserId { get; private set; }

    public DateTimeOffset LastStatusAt { get; private set; }
    public bool IsDemoData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public int Version { get; private set; }

    public static Result<Responder> Create(
        Guid id, string callSign, string fullName, string team, string? phone, string? email,
        IEnumerable<string> skills, Guid? userId, DateTimeOffset now, bool isDemoData = false)
    {
        var normalisedCallSign = NormaliseCallSign(callSign);
        if (normalisedCallSign is null)
        {
            return Error.Validation("responder.call_sign_invalid", "Call sign must be 2-20 letters, digits or hyphens.");
        }

        var responder = new Responder
        {
            Id = id,
            CallSign = normalisedCallSign,
            Availability = ResponderAvailability.Available,
            IsActive = true,
            UserId = userId,
            LastStatusAt = now,
            IsDemoData = isDemoData,
            CreatedAt = now,
        };
        var details = responder.ApplyDetails(fullName, team, phone, email, skills);
        return details.IsFailure ? details.Error! : responder;
    }

    public Result UpdateDetails(string fullName, string team, string? phone, string? email, IEnumerable<string> skills, Guid? userId)
    {
        var result = ApplyDetails(fullName, team, phone, email, skills);
        if (result.IsSuccess)
        {
            UserId = userId;
            Version++;
        }

        return result;
    }

    /// <summary>The responder or an Administrator sets Available/Unavailable, only when no assignment is active (10.4).</summary>
    public Result SetAvailability(ResponderAvailability availability, bool hasActiveAssignment, DateTimeOffset now)
    {
        if (availability == ResponderAvailability.Assigned)
        {
            return Error.Validation("responder.availability_derived", "Assigned is set automatically by an active assignment.");
        }

        if (!IsActive)
        {
            return Error.InvalidTransition("responder.inactive", "A deactivated responder cannot change availability.");
        }

        if (hasActiveAssignment)
        {
            return Error.InvalidTransition("responder.has_active_assignment", "Availability cannot change while an assignment is active.");
        }

        if (Availability != availability)
        {
            Availability = availability;
            LastStatusAt = now;
            Version++;
        }

        return Result.Success();
    }

    public Result Deactivate(bool hasActiveAssignment, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Conflict("responder.already_inactive", "The responder is already deactivated.");
        }

        if (hasActiveAssignment)
        {
            return Error.Conflict("responder.has_active_assignment", "End or cancel the responder's active assignment first.");
        }

        IsActive = false;
        Availability = ResponderAvailability.Unavailable;
        LastStatusAt = now;
        Version++;
        return Result.Success();
    }

    /// <summary>Reactivated responders start Unavailable until they (or an Administrator) confirm availability.</summary>
    public Result Activate(DateTimeOffset now)
    {
        if (IsActive)
        {
            return Error.Conflict("responder.already_active", "The responder is already active.");
        }

        IsActive = true;
        Availability = ResponderAvailability.Unavailable;
        LastStatusAt = now;
        Version++;
        return Result.Success();
    }

    /// <summary>System consequence of a new active assignment. Does not change <see cref="Version"/>.</summary>
    public void MarkAssigned(DateTimeOffset now)
    {
        if (Availability != ResponderAvailability.Assigned)
        {
            Availability = ResponderAvailability.Assigned;
            LastStatusAt = now;
        }
    }

    /// <summary>System consequence of the last active assignment ending: back to Available if still active.</summary>
    public void ReleaseFromAssignment(DateTimeOffset now)
    {
        if (Availability == ResponderAvailability.Assigned)
        {
            Availability = IsActive ? ResponderAvailability.Available : ResponderAvailability.Unavailable;
            LastStatusAt = now;
        }
    }

    public static string? NormaliseCallSign(string? callSign)
    {
        var value = callSign?.Trim().ToUpperInvariant();
        return value is not null && CallSignPattern().IsMatch(value) ? value : null;
    }

    private Result ApplyDetails(string fullName, string team, string? phone, string? email, IEnumerable<string> skills)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length > MaxNameLength)
        {
            return Error.Validation("responder.name_invalid", $"Name is required (at most {MaxNameLength} characters).");
        }

        if (string.IsNullOrWhiteSpace(team) || team.Trim().Length > MaxTeamLength)
        {
            return Error.Validation("responder.team_invalid", $"Team is required (at most {MaxTeamLength} characters).");
        }

        var normalisedSkills = (skills ?? [])
            .Select(s => s?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalisedSkills.Length > MaxSkills || normalisedSkills.Any(s => s.Length > MaxSkillLength))
        {
            return Error.Validation("responder.skills_invalid", $"At most {MaxSkills} skills of at most {MaxSkillLength} characters each.");
        }

        FullName = fullName.Trim();
        Team = team.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        Skills = normalisedSkills;
        return Result.Success();
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,19}$")]
    private static partial Regex CallSignPattern();
}
