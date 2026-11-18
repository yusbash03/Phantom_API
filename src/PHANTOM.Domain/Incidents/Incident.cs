using PHANTOM.Domain.Common;

namespace PHANTOM.Domain.Incidents;

/// <summary>
/// The operational incident record. Status changes only through the transition methods,
/// all of which consult <see cref="IncidentStatusPolicy"/>. AI analysis may only write the
/// <c>AiRecommended*</c> fields; approved values are written only by coordinator operations.
/// </summary>
public sealed class Incident
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 5000;
    public const int MaxReasonLength = 1000;

    private Incident()
    {
        // EF Core
        Number = string.Empty;
        Title = string.Empty;
        Description = string.Empty;
        Location = null!;
    }

    public Guid Id { get; private set; }
    public string Number { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public IncidentCategory Category { get; private set; }
    public IncidentCategory? AiRecommendedCategory { get; private set; }
    public IncidentStatus Status { get; private set; }
    public Severity? ApprovedSeverity { get; private set; }
    public Severity? AiRecommendedSeverity { get; private set; }
    public Location Location { get; private set; }
    public bool LocationNeedsVerification { get; private set; }
    public Guid? AssignedCoordinatorId { get; private set; }
    public Guid? VerifiedById { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? ResolutionSummary { get; private set; }
    public Guid? ResolvedById { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ClosureReason { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public bool IsDemoData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<Incident> Create(
        Guid id,
        string number,
        string title,
        string description,
        IncidentCategory category,
        Location location,
        DateTimeOffset now,
        bool isDemoData = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        var error = ValidateText(title, description);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            return Error.Validation("incident.number_required", "An incident number is required.");
        }

        return new Incident
        {
            Id = id,
            Number = number,
            Title = title.Trim(),
            Description = description.Trim(),
            Category = category,
            Status = IncidentStatus.Reported,
            Location = location,
            LocationNeedsVerification = !location.HasCoordinates,
            IsDemoData = isDemoData,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public Result BeginReview(Guid coordinatorId, DateTimeOffset now)
    {
        var result = Apply(IncidentTransition.BeginReview, now);
        if (result.IsSuccess)
        {
            AssignedCoordinatorId ??= coordinatorId;
        }

        return result;
    }

    public Result Verify(Severity approvedSeverity, Guid coordinatorId, DateTimeOffset now)
    {
        if (!IncidentStatusPolicy.TryGetRule(Status, IncidentTransition.Verify, out _))
        {
            return Invalid(IncidentTransition.Verify);
        }

        ApprovedSeverity = approvedSeverity;
        VerifiedById = coordinatorId;
        VerifiedAt = now;
        return Apply(IncidentTransition.Verify, now);
    }

    public Result Reject(string reason, DateTimeOffset now)
    {
        var error = ValidateReason(reason);
        if (error is not null)
        {
            return error;
        }

        var result = Apply(IncidentTransition.Reject, now);
        if (result.IsSuccess)
        {
            ClosureReason = reason.Trim();
            ClosedAt = now;
        }

        return result;
    }

    public Result CloseAsDuplicate(string primaryIncidentNumber, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(primaryIncidentNumber) || primaryIncidentNumber == Number)
        {
            return Error.Validation("incident.duplicate_primary_invalid", "A different primary incident is required.");
        }

        var result = Apply(IncidentTransition.CloseAsDuplicate, now);
        if (result.IsSuccess)
        {
            ClosureReason = $"Duplicate of {primaryIncidentNumber}";
            ClosedAt = now;
        }

        return result;
    }

    /// <summary>System consequence of a coordinator creating the first active assignment.</summary>
    public Result MarkAssigned(DateTimeOffset now) => Apply(IncidentTransition.FirstAssignmentCreated, now);

    /// <summary>System consequence of an assignee reporting they are en route.</summary>
    public Result MarkInProgress(DateTimeOffset now) =>
        Status == IncidentStatus.InProgress ? Result.Success() : Apply(IncidentTransition.ResponderEnRoute, now);

    /// <summary>System consequence of every assignment ending without completion.</summary>
    public Result RevertToVerified(DateTimeOffset now) => Apply(IncidentTransition.AllAssignmentsEnded, now);

    public Result Resolve(string resolutionSummary, Guid coordinatorId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(resolutionSummary))
        {
            return Error.Validation("incident.resolution_required", "Resolution details are required.");
        }

        if (resolutionSummary.Trim().Length > MaxDescriptionLength)
        {
            return Error.Validation("incident.resolution_too_long", $"Resolution details must be at most {MaxDescriptionLength} characters.");
        }

        var result = Apply(IncidentTransition.Resolve, now);
        if (result.IsSuccess)
        {
            ResolutionSummary = resolutionSummary.Trim();
            ResolvedById = coordinatorId;
            ResolvedAt = now;
        }

        return result;
    }

    public Result Reopen(string reason, DateTimeOffset now)
    {
        var error = ValidateReason(reason);
        if (error is not null)
        {
            return error;
        }

        var result = Apply(IncidentTransition.Reopen, now);
        if (result.IsSuccess)
        {
            ResolvedAt = null;
            ResolvedById = null;
        }

        return result;
    }

    public Result Close(DateTimeOffset now)
    {
        var result = Apply(IncidentTransition.Close, now);
        if (result.IsSuccess)
        {
            ClosedAt = now;
        }

        return result;
    }

    public Result ChangeApprovedSeverity(Severity severity, DateTimeOffset now)
    {
        if (ApprovedSeverity is null)
        {
            return Error.InvalidTransition("incident.not_verified", "Severity is approved when the incident is verified.");
        }

        if (IncidentStatusPolicy.IsTerminal(Status))
        {
            return Error.InvalidTransition("incident.terminal", "A closed or rejected incident cannot be changed.");
        }

        ApprovedSeverity = severity;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result EditDetails(string title, string description, IncidentCategory category, DateTimeOffset now)
    {
        if (IncidentStatusPolicy.IsTerminal(Status))
        {
            return Error.InvalidTransition("incident.terminal", "A closed or rejected incident cannot be changed.");
        }

        var error = ValidateText(title, description);
        if (error is not null)
        {
            return error;
        }

        Title = title.Trim();
        Description = description.Trim();
        Category = category;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Records an AI recommendation. Deliberately cannot touch approved values or status.</summary>
    public void RecordAiRecommendation(Severity severity, IncidentCategory category, DateTimeOffset now)
    {
        AiRecommendedSeverity = severity;
        AiRecommendedCategory = category;
        UpdatedAt = now;
    }

    public void UpdateLocation(Location location, DateTimeOffset now)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        LocationNeedsVerification = !location.IsVerified && !location.HasCoordinates;
        UpdatedAt = now;
    }

    public void FlagLocationForVerification(DateTimeOffset now)
    {
        LocationNeedsVerification = true;
        UpdatedAt = now;
    }

    private Result Apply(IncidentTransition transition, DateTimeOffset now)
    {
        if (!IncidentStatusPolicy.TryGetRule(Status, transition, out var rule))
        {
            return Invalid(transition);
        }

        Status = rule!.To;
        UpdatedAt = now;
        return Result.Success();
    }

    private Error Invalid(IncidentTransition transition) =>
        Error.InvalidTransition("incident.invalid_transition", $"Cannot {transition} an incident that is {Status}.");

    private static Error? ValidateText(string title, string description)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            return Error.Validation("incident.title_invalid", $"Title is required and must be at most {MaxTitleLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > MaxDescriptionLength)
        {
            return Error.Validation("incident.description_invalid", $"Description is required and must be at most {MaxDescriptionLength} characters.");
        }

        return null;
    }

    private static Error? ValidateReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength
            ? Error.Validation("incident.reason_required", $"A reason is required (at most {MaxReasonLength} characters).")
            : null;
}
