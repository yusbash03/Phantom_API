using FluentValidation;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Incidents;
using Severity = PHANTOM.Domain.Incidents.Severity;

namespace PHANTOM.Application.Assignments;

/// <summary>What the caller may do next with an assignment.</summary>
public enum AssignmentAction
{
    Acknowledge,
    Decline,
    EnRoute,
    OnScene,
    PostUpdate,
    Complete,
    Cancel,
}

public sealed record AssignedResponder(Guid Id, string CallSign, string FullName, string Team, bool HasLinkedAccount);

/// <summary>The incident fields a responder needs. Reporter contact details stay with staff.</summary>
public sealed record AssignedIncident(
    Guid Id,
    string Number,
    string Title,
    string Description,
    IncidentCategory Category,
    IncidentStatus Status,
    Severity? ApprovedSeverity,
    string LocationText,
    decimal? Latitude,
    decimal? Longitude,
    LocationProvenance LocationProvenance,
    bool LocationIsVerified);

public sealed record IncidentUpdateResponse(Guid Id, IncidentUpdateKind Kind, string Body, UserRef? Author, DateTimeOffset CreatedAt);

public sealed record AssignmentResponse(
    Guid Id,
    AssignmentStatus Status,
    AssignedResponder Responder,
    AssignedIncident Incident,
    UserRef? AssignedBy,
    DateTimeOffset AssignedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? EnRouteAt,
    DateTimeOffset? OnSceneAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? EndedAt,
    string? EndedReason,
    string? OverrideReason,
    string? CompletionNotes,
    IReadOnlyList<IncidentUpdateResponse> Updates,
    IReadOnlyList<AssignmentAction> AvailableActions);

/// <summary><c>OverrideReason</c> is required when the responder is inactive, unavailable or already assigned (11.2).</summary>
public sealed record AssignResponderRequest(Guid? ResponderId, string? OverrideReason);

public sealed record AssignmentReasonRequest(string Reason);

/// <summary>Responder movement: <c>EnRoute</c> or <c>OnScene</c>.</summary>
public sealed record AssignmentStatusRequest(AssignmentStatus? Status);

public sealed record AssignmentUpdateRequest(string Body);

public sealed record CompleteAssignmentRequest(string Notes);

internal sealed class AssignResponderRequestValidator : AbstractValidator<AssignResponderRequest>
{
    public AssignResponderRequestValidator()
    {
        RuleFor(r => r.ResponderId).NotNull().WithMessage("Choose a responder.");
        RuleFor(r => r.OverrideReason).MaximumLength(ResponderAssignment.MaxReasonLength);
    }
}

internal sealed class AssignmentReasonRequestValidator : AbstractValidator<AssignmentReasonRequest>
{
    public AssignmentReasonRequestValidator() =>
        RuleFor(r => r.Reason).NotEmpty().WithMessage("A reason is required.").MaximumLength(ResponderAssignment.MaxReasonLength);
}

internal sealed class AssignmentStatusRequestValidator : AbstractValidator<AssignmentStatusRequest>
{
    public AssignmentStatusRequestValidator() =>
        RuleFor(r => r.Status).NotNull().Must(s => s is AssignmentStatus.EnRoute or AssignmentStatus.OnScene)
            .WithMessage("Status must be EnRoute or OnScene.");
}

internal sealed class AssignmentUpdateRequestValidator : AbstractValidator<AssignmentUpdateRequest>
{
    public AssignmentUpdateRequestValidator() =>
        RuleFor(r => r.Body).NotEmpty().WithMessage("Write an update.").MaximumLength(IncidentUpdate.MaxBodyLength);
}

internal sealed class CompleteAssignmentRequestValidator : AbstractValidator<CompleteAssignmentRequest>
{
    public CompleteAssignmentRequestValidator() =>
        RuleFor(r => r.Notes).NotEmpty().WithMessage("Resolution notes are required.").MaximumLength(ResponderAssignment.MaxNotesLength);
}
