using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using Severity = PHANTOM.Domain.Incidents.Severity;

namespace PHANTOM.Application.Incidents;

public interface IIncidentService
{
    Task<Result<PagedResult<IncidentListItem>>> ListAsync(IncidentListQuery query, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> EditAsync(Guid id, EditIncidentRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> BeginReviewAsync(Guid id, VersionedRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> VerifyAsync(Guid id, VerifyIncidentRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> RejectAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> ResolveAsync(Guid id, ResolveIncidentRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> ReopenAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> CloseAsync(Guid id, VersionedRequest request, CancellationToken cancellationToken);
    Task<Result<IncidentDetailResponse>> ChangeSeverityAsync(Guid id, ChangeSeverityRequest request, CancellationToken cancellationToken);
    Task<Result<AttachmentContent>> GetAttachmentContentAsync(Guid attachmentId, CancellationToken cancellationToken);
}

internal sealed class IncidentService(
    IPhantomDbContext db,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock,
    IObjectStorage objectStorage,
    IServiceProvider services,
    ILogger<IncidentService> logger) : IIncidentService
{
    private static readonly Error NotFound = Error.NotFound("incident.not_found", "Incident not found.");
    private static readonly Error StaleVersion = Error.Conflict("incident.version_conflict", "This incident was changed by someone else. Reload and try again.");

    /// <summary>Coordinator-initiated transitions surfaced as actions. CloseAsDuplicate belongs to correlation decisions.</summary>
    private static readonly Dictionary<IncidentTransition, IncidentAction> TransitionActions = new()
    {
        [IncidentTransition.BeginReview] = IncidentAction.BeginReview,
        [IncidentTransition.Verify] = IncidentAction.Verify,
        [IncidentTransition.Reject] = IncidentAction.Reject,
        [IncidentTransition.Resolve] = IncidentAction.Resolve,
        [IncidentTransition.Reopen] = IncidentAction.Reopen,
        [IncidentTransition.Close] = IncidentAction.Close,
    };

    // Enums are stored as strings, so ranks are explicit for meaningful ordering.
    private static readonly Expression<Func<Incident, int>> SeverityRank = i =>
        i.ApprovedSeverity == Severity.Critical ? 4
        : i.ApprovedSeverity == Severity.High ? 3
        : i.ApprovedSeverity == Severity.Medium ? 2
        : i.ApprovedSeverity == Severity.Low ? 1
        : 0;

    private static readonly Expression<Func<Incident, int>> StatusRank = i =>
        i.Status == IncidentStatus.Reported ? 0
        : i.Status == IncidentStatus.UnderReview ? 1
        : i.Status == IncidentStatus.Verified ? 2
        : i.Status == IncidentStatus.Assigned ? 3
        : i.Status == IncidentStatus.InProgress ? 4
        : i.Status == IncidentStatus.Resolved ? 5
        : i.Status == IncidentStatus.Closed ? 6
        : 7;

    public async Task<Result<PagedResult<IncidentListItem>>> ListAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var validation = await Validate(query, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var incidents = db.Incidents.AsNoTracking();

        if (query.Status is { Count: > 0 })
        {
            var statuses = query.Status.Distinct().ToList();
            incidents = incidents.Where(i => statuses.Contains(i.Status));
        }

        if (query.Category.HasValue)
        {
            incidents = incidents.Where(i => i.Category == query.Category.Value);
        }

        if (query.Severity.HasValue)
        {
            incidents = incidents.Where(i => i.ApprovedSeverity == query.Severity.Value);
        }

        if (query.From.HasValue)
        {
            var from = query.From.Value.ToUniversalTime();
            incidents = incidents.Where(i => i.CreatedAt >= from);
        }

        if (query.To.HasValue)
        {
            var to = query.To.Value.ToUniversalTime();
            incidents = incidents.Where(i => i.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Expression tree translated to SQL lower()/LIKE, not executed in .NET.
            incidents = incidents.Where(i =>
                i.Number.ToLower().Contains(term)
                || i.Title.ToLower().Contains(term)
                || i.Description.ToLower().Contains(term)
                || i.Location.Text.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await incidents.CountAsync(cancellationToken);
        var ascending = query.Direction == SortDirection.Asc;
        var ordered = query.Sort switch
        {
            IncidentSortField.UpdatedAt => ascending ? incidents.OrderBy(i => i.UpdatedAt) : incidents.OrderByDescending(i => i.UpdatedAt),
            IncidentSortField.Severity => ascending ? incidents.OrderBy(SeverityRank) : incidents.OrderByDescending(SeverityRank),
            IncidentSortField.Status => ascending ? incidents.OrderBy(StatusRank) : incidents.OrderByDescending(StatusRank),
            _ => ascending ? incidents.OrderBy(i => i.CreatedAt) : incidents.OrderByDescending(i => i.CreatedAt),
        };

        var items = await ordered
            .ThenByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Select(i => new IncidentListItem(
                i.Id,
                i.Number,
                i.Title,
                i.Category,
                i.Status,
                i.ApprovedSeverity,
                i.AiRecommendedSeverity,
                i.Location.Text,
                i.Location.Latitude != null && i.Location.Longitude != null,
                i.Location.Provenance == LocationProvenance.ManuallyVerified,
                i.LocationNeedsVerification,
                db.IncidentReports.Count(r => r.IncidentId == i.Id),
                i.IsDemoData,
                i.CreatedAt,
                i.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<IncidentListItem>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<Result<IncidentDetailResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return NotFound;
        }

        var reports = await db.IncidentReports.AsNoTracking()
            .Where(r => r.IncidentId == id)
            .OrderBy(r => r.SubmittedAt)
            .ToListAsync(cancellationToken);
        var reportIds = reports.Select(r => r.Id).ToList();
        var attachments = (await db.Attachments.AsNoTracking()
                .Where(a => reportIds.Contains(a.ReportId))
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.ReportId);

        Guid?[] userIds = [incident.AssignedCoordinatorId, incident.VerifiedById, incident.ResolvedById, incident.Location.VerifiedById];
        var ids = userIds.OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        UserRef? Ref(Guid? userId) => userId.HasValue && users.TryGetValue(userId.Value, out var user) ? user : null;

        var location = incident.Location;
        return new IncidentDetailResponse(
            incident.Id,
            incident.Number,
            incident.Title,
            incident.Description,
            incident.Category,
            incident.AiRecommendedCategory,
            incident.Status,
            incident.ApprovedSeverity,
            incident.AiRecommendedSeverity,
            new LocationResponse(location.Text, location.Latitude, location.Longitude, location.Provenance, location.Precision,
                location.IsVerified, Ref(location.VerifiedById), location.VerifiedAt),
            incident.LocationNeedsVerification,
            Ref(incident.AssignedCoordinatorId),
            Ref(incident.VerifiedById),
            incident.VerifiedAt,
            incident.ResolutionSummary,
            Ref(incident.ResolvedById),
            incident.ResolvedAt,
            incident.ClosureReason,
            incident.ClosedAt,
            incident.IsDemoData,
            incident.CreatedAt,
            incident.UpdatedAt,
            incident.Version,
            AvailableActions(incident),
            reports.Select(r => new IncidentReportResponse(
                r.Id,
                r.Reference,
                r.OriginalIncidentId == incident.Id,
                r.Title,
                r.Description,
                r.Category,
                r.LocationText,
                r.ReportedLatitude,
                r.ReportedLongitude,
                r.ReporterName,
                r.ReporterPhone,
                r.ReporterEmail,
                r.SubmittedAt,
                attachments[r.Id].Select(a => new AttachmentResponse(a.Id, a.Kind, a.ContentType, a.SizeBytes, a.OriginalFileName, a.CreatedAt)).ToList()))
            .ToList());
    }

    public Task<Result<IncidentDetailResponse>> EditAsync(Guid id, EditIncidentRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, request, request.Version, cancellationToken: cancellationToken, apply: (incident, _, now) =>
        {
            var before = new { incident.Title, incident.Description, Category = incident.Category.ToString() };
            var result = incident.EditDetails(request.Title, request.Description, request.Category!.Value, now);
            if (result.IsSuccess)
            {
                // The original IncidentReport rows are untouched; only the working incident record changes.
                audit.Record(AuditActions.IncidentUpdated, nameof(Incident), incident.Id.ToString(), before,
                    new { incident.Title, incident.Description, Category = incident.Category.ToString() });
            }

            return result;
        });

    public Task<Result<IncidentDetailResponse>> BeginReviewAsync(Guid id, VersionedRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.BeginReview, null, cancellationToken: cancellationToken,
            apply: (incident, actor, now) => incident.BeginReview(actor, now));

    public Task<Result<IncidentDetailResponse>> VerifyAsync(Guid id, VerifyIncidentRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.Verify, new { approvedSeverity = request.ApprovedSeverity?.ToString() }, cancellationToken: cancellationToken,
            apply: (incident, actor, now) => incident.Verify(request.ApprovedSeverity!.Value, actor, now));

    public Task<Result<IncidentDetailResponse>> RejectAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.Reject, new { reason = request.Reason.Trim() }, cancellationToken: cancellationToken,
            apply: (incident, _, now) => incident.Reject(request.Reason, now));

    public Task<Result<IncidentDetailResponse>> ResolveAsync(Guid id, ResolveIncidentRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.Resolve, new { resolutionSummary = request.ResolutionSummary.Trim() }, cancellationToken: cancellationToken,
            apply: (incident, actor, now) => incident.Resolve(request.ResolutionSummary, actor, now));

    public Task<Result<IncidentDetailResponse>> ReopenAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.Reopen, new { reason = request.Reason.Trim() }, cancellationToken: cancellationToken,
            apply: (incident, _, now) => incident.Reopen(request.Reason, now));

    public Task<Result<IncidentDetailResponse>> CloseAsync(Guid id, VersionedRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, request, request.Version, IncidentTransition.Close, null, cancellationToken: cancellationToken,
            apply: (incident, _, now) => incident.Close(now));

    public Task<Result<IncidentDetailResponse>> ChangeSeverityAsync(Guid id, ChangeSeverityRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, request, request.Version, cancellationToken: cancellationToken, apply: (incident, _, now) =>
        {
            var previous = incident.ApprovedSeverity;
            var result = incident.ChangeApprovedSeverity(request.Severity!.Value, now);
            if (result.IsSuccess && previous != request.Severity)
            {
                audit.Record(AuditActions.IncidentSeverityChanged, nameof(Incident), incident.Id.ToString(),
                    new { approvedSeverity = previous?.ToString() }, new { approvedSeverity = request.Severity?.ToString() });
            }

            return result;
        });

    public async Task<Result<AttachmentContent>> GetAttachmentContentAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return Error.NotFound("attachment.not_found", "Attachment not found.");
        }

        try
        {
            var content = await objectStorage.RetrieveAsync(attachment.StorageKey, cancellationToken);
            return new AttachmentContent(content, attachment.ContentType, attachment.OriginalFileName);
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError(ex, "Attachment {AttachmentId} is recorded but its stored object is missing", attachmentId);
            return Error.NotFound("attachment.content_missing", "The attachment content is not available.");
        }
    }

    private Task<Result<IncidentDetailResponse>> TransitionAsync<TRequest>(
        Guid id,
        TRequest request,
        uint version,
        IncidentTransition transition,
        object? details,
        Func<Incident, Guid, DateTimeOffset, Result> apply,
        CancellationToken cancellationToken) =>
        MutateAsync(id, request, version, cancellationToken: cancellationToken, apply: (incident, actor, now) =>
        {
            var from = incident.Status;
            var result = apply(incident, actor, now);
            if (result.IsSuccess)
            {
                audit.Record(AuditActions.IncidentStatusChanged, nameof(Incident), incident.Id.ToString(),
                    new { status = from.ToString() },
                    new { status = incident.Status.ToString() },
                    new { transition = transition.ToString(), details });
            }

            return result;
        });

    private async Task<Result<IncidentDetailResponse>> MutateAsync<TRequest>(
        Guid id,
        TRequest request,
        uint version,
        Func<Incident, Guid, DateTimeOffset, Result> apply,
        CancellationToken cancellationToken)
    {
        var validation = await Validate(request, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        if (currentUser.UserId is not { } actorId)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return NotFound;
        }

        // The loaded xmin is also the concurrency token for SaveChanges, so a write that lands
        // between this check and the save still fails with a conflict.
        if (incident.Version != version)
        {
            return StaleVersion;
        }

        var result = apply(incident, actorId, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private async Task<Error?> Validate<TRequest>(TRequest request, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(IValidator<TRequest>)) is not IValidator<TRequest> validator)
        {
            return null;
        }

        var result = await validator.ValidateAsync(request, cancellationToken);
        return result.IsValid ? null : ValidationFailedError.From(result);
    }

    private List<IncidentAction> AvailableActions(Incident incident)
    {
        if (!currentUser.IsInRole(RoleNames.IncidentCoordinator))
        {
            return [];
        }

        var actions = new List<IncidentAction>();
        var terminal = IncidentStatusPolicy.IsTerminal(incident.Status);
        if (!terminal)
        {
            actions.Add(IncidentAction.Edit);
        }

        actions.AddRange(IncidentStatusPolicy.Rules
            .Where(r => r.From == incident.Status && r.Actor == TransitionActor.Coordinator && TransitionActions.ContainsKey(r.Transition))
            .Select(r => TransitionActions[r.Transition]));

        if (!terminal && incident.ApprovedSeverity.HasValue)
        {
            actions.Add(IncidentAction.ChangeSeverity);
        }

        return actions;
    }
}
