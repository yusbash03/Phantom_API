using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;
using PHANTOM.Application.Notifications;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Notifications;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Application.Assignments;

public interface IAssignmentService
{
    Task<Result<AssignmentResponse>> AssignAsync(Guid incidentId, AssignResponderRequest request, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<AssignmentResponse>>> ListForIncidentAsync(Guid incidentId, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> CancelAsync(Guid id, AssignmentReasonRequest request, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssignmentResponse>> MineAsync(CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> AcknowledgeAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> DeclineAsync(Guid id, AssignmentReasonRequest request, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> SetStatusAsync(Guid id, AssignmentStatusRequest request, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> PostUpdateAsync(Guid id, AssignmentUpdateRequest request, CancellationToken cancellationToken);
    Task<Result<AssignmentResponse>> CompleteAsync(Guid id, CompleteAssignmentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The assignment workflow (Requirement 11). Assignments are created only by a coordinator; only the
/// assigned responder's linked user may progress them (others get 404). Incident and responder state
/// that follows from assignments is changed here, in the same unit of work, and audited as System.
/// </summary>
internal sealed class AssignmentService(
    IPhantomDbContext db,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock,
    INotifier notifier,
    IServiceProvider services) : IAssignmentService
{
    private const int MineLimit = 50;
    private static readonly Error NotFound = Error.NotFound("assignment.not_found", "Assignment not found.");
    private static readonly IncidentStatus[] AssignableStatuses = [IncidentStatus.Verified, IncidentStatus.Assigned, IncidentStatus.InProgress];

    public async Task<Result<AssignmentResponse>> AssignAsync(Guid incidentId, AssignResponderRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        if (currentUser.UserId is not { } coordinatorId)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null)
        {
            return Error.NotFound("incident.not_found", "Incident not found.");
        }

        if (!AssignableStatuses.Contains(incident.Status))
        {
            return Error.InvalidTransition("assignment.incident_not_assignable",
                $"{incident.Number} is {incident.Status}; only Verified, Assigned or In progress incidents can be assigned.");
        }

        var responder = await db.Responders.FirstOrDefaultAsync(r => r.Id == request.ResponderId, cancellationToken);
        if (responder is null)
        {
            return Error.NotFound("responder.not_found", "Responder not found.");
        }

        if (await Active().AnyAsync(a => a.IncidentId == incidentId && a.ResponderId == responder.Id, cancellationToken))
        {
            return Error.Conflict("assignment.duplicate", $"{responder.CallSign} is already assigned to {incident.Number}.");
        }

        // Requirement 11.2: an unsuitable responder needs an explicit, audited override.
        var problems = new List<string>();
        if (!responder.IsActive)
        {
            problems.Add("deactivated");
        }

        if (await Active().AnyAsync(a => a.ResponderId == responder.Id, cancellationToken))
        {
            problems.Add("already on another active assignment");
        }

        if (responder.IsActive && responder.Availability == ResponderAvailability.Unavailable)
        {
            problems.Add("marked unavailable");
        }

        var overrideReason = string.IsNullOrWhiteSpace(request.OverrideReason) ? null : request.OverrideReason.Trim();
        if (problems.Count > 0 && overrideReason is null)
        {
            return Error.Conflict("assignment.override_required",
                $"{responder.CallSign} is {string.Join(" and ", problems)}. Give an override reason to assign anyway.");
        }

        var now = clock.UtcNow;
        var assignment = ResponderAssignment.Create(Guid.CreateVersion7(), incidentId, responder.Id, coordinatorId, now, problems.Count > 0 ? overrideReason : null);
        db.ResponderAssignments.Add(assignment);
        audit.Record(AuditActions.AssignmentCreated, nameof(ResponderAssignment), assignment.Id.ToString(),
            newValue: new { incidentId, incident.Number, responderId = responder.Id, responder.CallSign, status = assignment.Status.ToString() },
            metadata: problems.Count > 0 ? new { overrideReason = assignment.OverrideReason, overriddenChecks = problems } : null);

        SetResponderAssigned(responder, assignment.Id, now);
        if (responder.UserId is { } responderUserId)
        {
            notifier.NotifyUser(responderUserId, NotificationType.AssignmentCreated, $"New assignment: {incident.Number}",
                $"{incident.Title} — {incident.Location.Text}. Acknowledge it in My assignments.", incident.Id, assignment.Id);
        }

        if (incident.Status == IncidentStatus.Verified)
        {
            ApplySystemTransition(incident, IncidentTransition.FirstAssignmentCreated, i => i.MarkAssigned(now), assignment.Id);
        }

        return await SaveAsync(assignment, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<AssignmentResponse>>> ListForIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, cancellationToken))
        {
            return Error.NotFound("incident.not_found", "Incident not found.");
        }

        var assignments = await db.ResponderAssignments.AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .OrderByDescending(a => a.AssignedAt)
            .ToListAsync(cancellationToken);
        return await ToResponsesAsync(assignments, cancellationToken);
    }

    public async Task<Result<AssignmentResponse>> CancelAsync(Guid id, AssignmentReasonRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var assignment = await db.ResponderAssignments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assignment is null)
        {
            return NotFound;
        }

        var from = assignment.Status;
        var result = assignment.Cancel(request.Reason, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        RecordStatusChange(assignment, from, request.Reason);
        await ApplyEndedEffectsAsync(assignment, cancellationToken);
        var (responderUserId, _, incidentNumber) = await NotificationContextAsync(assignment, cancellationToken);
        if (responderUserId is { } userId)
        {
            notifier.NotifyUser(userId, NotificationType.AssignmentCancelled, $"Assignment cancelled: {incidentNumber}",
                $"Reason: {request.Reason.Trim()}", assignment.IncidentId, assignment.Id);
        }

        return await SaveAsync(assignment, cancellationToken);
    }

    public async Task<Result<AssignmentResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var assignment = await db.ResponderAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assignment is null || !(IsStaff() || await IsAssigneeAsync(assignment.ResponderId, cancellationToken)))
        {
            return NotFound;
        }

        return (await ToResponsesAsync([assignment], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<AssignmentResponse>> MineAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var responderIds = await db.Responders.Where(r => r.UserId == userId).Select(r => r.Id).ToListAsync(cancellationToken);
        var assignments = await db.ResponderAssignments.AsNoTracking()
            .Where(a => responderIds.Contains(a.ResponderId))
            .OrderBy(a => a.Status == AssignmentStatus.Completed || a.Status == AssignmentStatus.Declined || a.Status == AssignmentStatus.Cancelled)
            .ThenByDescending(a => a.AssignedAt)
            .Take(MineLimit)
            .ToListAsync(cancellationToken);
        return await ToResponsesAsync(assignments, cancellationToken);
    }

    public Task<Result<AssignmentResponse>> AcknowledgeAsync(Guid id, CancellationToken cancellationToken) =>
        AsAssigneeAsync(id, null, transition: (assignment, _, now) => Task.FromResult(assignment.Acknowledge(now)), consequences: null, cancellationToken: cancellationToken);

    public async Task<Result<AssignmentResponse>> DeclineAsync(Guid id, AssignmentReasonRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        return await AsAssigneeAsync(id, request.Reason, cancellationToken: cancellationToken,
            transition: (assignment, _, now) => Task.FromResult(assignment.Decline(request.Reason, now)),
            consequences: async (assignment, _, _) =>
            {
                await ApplyEndedEffectsAsync(assignment, cancellationToken);
                var (_, callSign, incidentNumber) = await NotificationContextAsync(assignment, cancellationToken);
                await notifier.NotifyCoordinatorsAsync(NotificationType.AssignmentDeclined, $"{callSign} declined {incidentNumber}",
                    $"Reason: {Sentence(request.Reason)} The incident needs another responder.", assignment.IncidentId, assignment.Id, cancellationToken);
            });
    }

    public async Task<Result<AssignmentResponse>> SetStatusAsync(Guid id, AssignmentStatusRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        if (request.Status == AssignmentStatus.OnScene)
        {
            return await AsAssigneeAsync(id, null, transition: (assignment, _, now) => Task.FromResult(assignment.MarkOnScene(now)), consequences: null, cancellationToken: cancellationToken);
        }

        return await AsAssigneeAsync(id, null, cancellationToken: cancellationToken,
            transition: (assignment, _, now) => Task.FromResult(assignment.MarkEnRoute(now)),
            consequences: async (assignment, _, now) =>
            {
                // Design §3.3: the first assignee en route moves the incident to In progress.
                var incident = await db.Incidents.FirstAsync(i => i.Id == assignment.IncidentId, cancellationToken);
                if (incident.Status == IncidentStatus.Assigned)
                {
                    ApplySystemTransition(incident, IncidentTransition.ResponderEnRoute, i => i.MarkInProgress(now), assignment.Id);
                }
            });
    }

    public async Task<Result<AssignmentResponse>> PostUpdateAsync(Guid id, AssignmentUpdateRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var loaded = await LoadAsAssigneeAsync(id, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (assignment, userId) = loaded.Value;
        if (!assignment.IsActive || assignment.Status == AssignmentStatus.Assigned)
        {
            return Error.InvalidTransition("assignment.updates_closed", "Updates can be posted once the assignment is acknowledged and until it ends.");
        }

        var update = IncidentUpdate.Create(Guid.CreateVersion7(), assignment.IncidentId, assignment.Id, userId, IncidentUpdateKind.ProgressUpdate, request.Body, clock.UtcNow);
        db.IncidentUpdates.Add(update);
        audit.Record(AuditActions.AssignmentUpdatePosted, nameof(ResponderAssignment), assignment.Id.ToString(),
            newValue: new { updateId = update.Id, kind = update.Kind.ToString(), incidentId = assignment.IncidentId });
        return await SaveAsync(assignment, cancellationToken);
    }

    public async Task<Result<AssignmentResponse>> CompleteAsync(Guid id, CompleteAssignmentRequest request, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        return await AsAssigneeAsync(id, null, cancellationToken: cancellationToken,
            transition: (assignment, _, now) => Task.FromResult(assignment.Complete(request.Notes, now)),
            consequences: async (assignment, userId, _) =>
            {
                // Requirement 11.8: the responder's notes are kept; only a coordinator resolves the incident.
                // Timestamped when written, so the history shows the completion and then its notes.
                db.IncidentUpdates.Add(IncidentUpdate.Create(Guid.CreateVersion7(), assignment.IncidentId, assignment.Id, userId,
                    IncidentUpdateKind.ResolutionNote, request.Notes, clock.UtcNow));
                await ApplyEndedEffectsAsync(assignment, cancellationToken);
                var (_, callSign, incidentNumber) = await NotificationContextAsync(assignment, cancellationToken);
                await notifier.NotifyCoordinatorsAsync(NotificationType.AssignmentCompleted, $"{callSign} completed their assignment on {incidentNumber}",
                    $"{Sentence(request.Notes)} Review and resolve the incident when appropriate.", assignment.IncidentId, assignment.Id, cancellationToken);
            });
    }

    // ── Shared workflow pieces ─────────────────────────────────────────────────

    /// <summary>
    /// Runs an assignee action as: domain transition, then its audit record, then its consequences
    /// (incident transitions, responder release, notifications), so the audit trail reads cause → effect.
    /// </summary>
    private async Task<Result<AssignmentResponse>> AsAssigneeAsync(
        Guid id,
        string? reason,
        Func<ResponderAssignment, Guid, DateTimeOffset, Task<Result>> transition,
        Func<ResponderAssignment, Guid, DateTimeOffset, Task>? consequences,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsAssigneeAsync(id, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (assignment, userId) = loaded.Value;
        var from = assignment.Status;
        var now = clock.UtcNow;
        var result = await transition(assignment, userId, now);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        RecordStatusChange(assignment, from, reason);
        if (consequences is not null)
        {
            await consequences(assignment, userId, now);
        }

        return await SaveAsync(assignment, cancellationToken);
    }

    private async Task<Result<(ResponderAssignment Assignment, Guid UserId)>> LoadAsAssigneeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotFound;
        }

        var assignment = await db.ResponderAssignments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assignment is null || !await IsAssigneeAsync(assignment.ResponderId, cancellationToken))
        {
            return NotFound;
        }

        return (assignment, userId);
    }

    /// <summary>When an assignment ends: free the responder if nothing else is active, and revert the incident if it never got going.</summary>
    private async Task ApplyEndedEffectsAsync(ResponderAssignment ended, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var responderStillBusy = await Active().AnyAsync(a => a.ResponderId == ended.ResponderId && a.Id != ended.Id, cancellationToken);
        if (!responderStillBusy)
        {
            var responder = await db.Responders.FirstAsync(r => r.Id == ended.ResponderId, cancellationToken);
            var before = responder.Availability;
            responder.ReleaseFromAssignment(now);
            if (before != responder.Availability)
            {
                audit.RecordSystem(AuditActions.ResponderAvailabilityChanged, nameof(Responder), responder.Id.ToString(),
                    new { availability = before.ToString() }, new { availability = responder.Availability.ToString() }, new { assignmentId = ended.Id, triggeredByUserId = currentUser.UserId });
            }
        }

        var incident = await db.Incidents.FirstAsync(i => i.Id == ended.IncidentId, cancellationToken);
        var incidentStillActive = await Active().AnyAsync(a => a.IncidentId == ended.IncidentId && a.Id != ended.Id, cancellationToken);
        if (incident.Status == IncidentStatus.Assigned && !incidentStillActive && ended.Status != AssignmentStatus.Completed)
        {
            ApplySystemTransition(incident, IncidentTransition.AllAssignmentsEnded, i => i.RevertToVerified(now), ended.Id);
        }
    }

    /// <summary>Free text as a sentence: trimmed, ending with exactly one full stop (or its own ! / ?).</summary>
    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ? trimmed : trimmed + ".";
    }

    private async Task<(Guid? ResponderUserId, string CallSign, string IncidentNumber)> NotificationContextAsync(ResponderAssignment assignment, CancellationToken cancellationToken)
    {
        var responder = await db.Responders.AsNoTracking().Where(r => r.Id == assignment.ResponderId)
            .Select(r => new { r.UserId, r.CallSign }).FirstAsync(cancellationToken);
        var number = await db.Incidents.AsNoTracking().Where(i => i.Id == assignment.IncidentId).Select(i => i.Number).FirstAsync(cancellationToken);
        return (responder.UserId, responder.CallSign, number);
    }

    private void SetResponderAssigned(Responder responder, Guid assignmentId, DateTimeOffset now)
    {
        var before = responder.Availability;
        responder.MarkAssigned(now);
        if (before != responder.Availability)
        {
            audit.RecordSystem(AuditActions.ResponderAvailabilityChanged, nameof(Responder), responder.Id.ToString(),
                new { availability = before.ToString() }, new { availability = responder.Availability.ToString() }, new { assignmentId, triggeredByUserId = currentUser.UserId });
        }
    }

    /// <summary>Design §3.3: system-driven incident transitions are audited as System, pointing at the human action.</summary>
    private void ApplySystemTransition(Incident incident, IncidentTransition transition, Func<Incident, Result> apply, Guid assignmentId)
    {
        var from = incident.Status;
        if (apply(incident).IsSuccess && from != incident.Status)
        {
            audit.RecordSystem(AuditActions.IncidentStatusChanged, nameof(Incident), incident.Id.ToString(),
                new { status = from.ToString() }, new { status = incident.Status.ToString() },
                new { transition = transition.ToString(), details = new { assignmentId, triggeredByUserId = currentUser.UserId } });
        }
    }

    private void RecordStatusChange(ResponderAssignment assignment, AssignmentStatus from, string? reason)
    {
        if (from != assignment.Status)
        {
            audit.Record(AuditActions.AssignmentStatusChanged, nameof(ResponderAssignment), assignment.Id.ToString(),
                new { status = from.ToString() }, new { status = assignment.Status.ToString(), reason = reason?.Trim() });
        }
    }

    private async Task<Result<AssignmentResponse>> SaveAsync(ResponderAssignment assignment, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("assignment.version_conflict", "This assignment, its incident or its responder changed meanwhile. Reload and try again.");
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("assignment.duplicate", "That responder was just assigned to this incident by someone else.");
        }

        return (await ToResponsesAsync([assignment], cancellationToken))[0];
    }

    private IQueryable<ResponderAssignment> Active() =>
        db.ResponderAssignments.Where(a => a.Status != AssignmentStatus.Completed && a.Status != AssignmentStatus.Declined && a.Status != AssignmentStatus.Cancelled);

    private bool IsStaff() => currentUser.IsInRole(RoleNames.Administrator) || currentUser.IsInRole(RoleNames.IncidentCoordinator);

    private async Task<bool> IsAssigneeAsync(Guid responderId, CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId && await db.Responders.AnyAsync(r => r.Id == responderId && r.UserId == userId, cancellationToken);

    private async Task<Error?> ValidateAsync<TRequest>(TRequest request, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(IValidator<TRequest>)) is not IValidator<TRequest> validator)
        {
            return null;
        }

        var result = await validator.ValidateAsync(request, cancellationToken);
        return result.IsValid ? null : ValidationFailedError.From(result);
    }

    private async Task<List<AssignmentResponse>> ToResponsesAsync(List<ResponderAssignment> assignments, CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return [];
        }

        var responderIds = assignments.Select(a => a.ResponderId).Distinct().ToList();
        var incidentIds = assignments.Select(a => a.IncidentId).Distinct().ToList();
        var assignmentIds = assignments.Select(a => a.Id).ToList();
        var responders = await db.Responders.AsNoTracking().Where(r => responderIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var incidents = await db.Incidents.AsNoTracking().Where(i => incidentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var updates = (await db.IncidentUpdates.AsNoTracking()
                .Where(u => u.AssignmentId != null && assignmentIds.Contains(u.AssignmentId.Value))
                .OrderBy(u => u.CreatedAt)
                .ToListAsync(cancellationToken))
            .ToLookup(u => u.AssignmentId!.Value);
        var userIds = assignments.Select(a => a.AssignedById).Concat(updates.SelectMany(g => g).Select(u => u.AuthorId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        UserRef? Ref(Guid id) => users.TryGetValue(id, out var user) ? user : null;
        var isCoordinator = currentUser.IsInRole(RoleNames.IncidentCoordinator);

        return assignments.Select(a =>
        {
            var responder = responders[a.ResponderId];
            var incident = incidents[a.IncidentId];
            var isAssignee = currentUser.UserId is { } userId && responder.UserId == userId;
            return new AssignmentResponse(
                a.Id,
                a.Status,
                new AssignedResponder(responder.Id, responder.CallSign, responder.FullName, responder.Team, responder.UserId is not null),
                new AssignedIncident(incident.Id, incident.Number, incident.Title, incident.Description, incident.Category, incident.Status,
                    incident.ApprovedSeverity, incident.Location.Text, incident.Location.Latitude, incident.Location.Longitude,
                    incident.Location.Provenance, incident.Location.IsVerified),
                Ref(a.AssignedById),
                a.AssignedAt, a.AcknowledgedAt, a.EnRouteAt, a.OnSceneAt, a.CompletedAt, a.EndedAt,
                a.EndedReason, a.OverrideReason, a.CompletionNotes,
                updates[a.Id].Select(u => new IncidentUpdateResponse(u.Id, u.Kind, u.Body, Ref(u.AuthorId), u.CreatedAt)).ToList(),
                Actions(a, isAssignee, isCoordinator));
        }).ToList();
    }

    private static List<AssignmentAction> Actions(ResponderAssignment a, bool isAssignee, bool isCoordinator)
    {
        var actions = new List<AssignmentAction>();
        if (isAssignee)
        {
            actions.AddRange(a.Status switch
            {
                AssignmentStatus.Assigned => [AssignmentAction.Acknowledge, AssignmentAction.Decline],
                AssignmentStatus.Acknowledged => [AssignmentAction.EnRoute, AssignmentAction.PostUpdate, AssignmentAction.Decline],
                AssignmentStatus.EnRoute => [AssignmentAction.OnScene, AssignmentAction.PostUpdate],
                AssignmentStatus.OnScene => [AssignmentAction.Complete, AssignmentAction.PostUpdate],
                _ => (AssignmentAction[])[],
            });
        }

        if (isCoordinator && a.IsActive)
        {
            actions.Add(AssignmentAction.Cancel);
        }

        return actions;
    }
}
