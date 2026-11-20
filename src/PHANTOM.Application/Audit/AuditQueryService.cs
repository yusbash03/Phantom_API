using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Audit;

public enum TimelineEventKind
{
    Report,
    Ai,
    Decision,
    Assignment,
    Update,
    Correlation,
    Location,
    System,
}

/// <summary>
/// One entry in an incident's history (Requirement 15.4). <c>IsAi</c> marks AI output so it is never
/// mistaken for a human decision; <c>ActorType</c> says whether a person, the system or the public acted.
/// </summary>
public sealed record TimelineEvent(
    string Id,
    DateTimeOffset OccurredAt,
    TimelineEventKind Kind,
    AuditActorType ActorType,
    UserRef? Actor,
    string Action,
    string Summary,
    string? Detail,
    bool IsAi);

public sealed record AuditSearchQuery(
    Guid? ActorUserId = null,
    string? EntityType = null,
    string? EntityId = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>An audit record as stored. JSON values are passed through unchanged for inspection.</summary>
public sealed record AuditEntryResponse(
    long Id,
    DateTimeOffset OccurredAt,
    AuditActorType ActorType,
    UserRef? Actor,
    string Action,
    string EntityType,
    string? EntityId,
    string? PreviousValue,
    string? NewValue,
    string? Metadata,
    string? CorrelationId);

public sealed record AuditFacets(IReadOnlyList<string> Actions, IReadOnlyList<string> EntityTypes);

public interface IAuditQueryService
{
    Task<Result<IReadOnlyList<TimelineEvent>>> TimelineAsync(Guid incidentId, CancellationToken cancellationToken);
    Task<Result<PagedResult<AuditEntryResponse>>> SearchAsync(AuditSearchQuery query, CancellationToken cancellationToken);
    Task<AuditFacets> FacetsAsync(CancellationToken cancellationToken);
}

internal sealed class AuditSearchQueryValidator : AbstractValidator<AuditSearchQuery>
{
    public AuditSearchQueryValidator()
    {
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end of the date range must be after its start.");
        RuleFor(q => q.EntityType).MaximumLength(100);
        RuleFor(q => q.EntityId).MaximumLength(100);
        RuleFor(q => q.Action).MaximumLength(100);
    }
}

/// <summary>Read side of the append-only audit log: incident timelines (15.4, 15.5) and admin search (15.6).</summary>
internal sealed class AuditQueryService(IPhantomDbContext db, IValidator<AuditSearchQuery> searchValidator) : IAuditQueryService
{
    private static readonly IReadOnlyList<string> KnownActions = typeof(AuditActions)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Recorded for completeness but shown via the update itself, so not repeated on the timeline.</summary>
    private static readonly HashSet<string> HiddenFromTimeline = [AuditActions.AssignmentUpdatePosted];

    public async Task<Result<IReadOnlyList<TimelineEvent>>> TimelineAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().Where(i => i.Id == incidentId).Select(i => new { i.Id, i.Number }).FirstOrDefaultAsync(cancellationToken);
        if (incident is null)
        {
            return Error.NotFound("incident.not_found", "Incident not found.");
        }

        // Everything that belongs to this incident's story, including reports later merged in or out.
        var reports = await db.IncidentReports.AsNoTracking()
            .Where(r => r.IncidentId == incidentId || r.OriginalIncidentId == incidentId)
            .Select(r => new { r.Id, r.Reference }).ToListAsync(cancellationToken);
        var analysisIds = await db.IncidentAnalyses.AsNoTracking().Where(a => a.IncidentId == incidentId).Select(a => a.Id).ToListAsync(cancellationToken);
        var assignments = await db.ResponderAssignments.AsNoTracking().Where(a => a.IncidentId == incidentId)
            .Join(db.Responders, a => a.ResponderId, r => r.Id, (a, r) => new { a.Id, r.CallSign }).ToListAsync(cancellationToken);
        var relations = await db.IncidentRelations.AsNoTracking()
            .Where(r => r.SourceIncidentId == incidentId || r.TargetIncidentId == incidentId)
            .Select(r => new { r.Id, Other = r.SourceIncidentId == incidentId ? r.TargetIncidentId : r.SourceIncidentId }).ToListAsync(cancellationToken);
        var otherIds = relations.Select(r => r.Other).Distinct().ToList();
        var otherNumbers = await db.Incidents.AsNoTracking().Where(i => otherIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, cancellationToken);

        var context = new TimelineContext(
            reports.ToDictionary(r => r.Id.ToString(), r => r.Reference),
            assignments.ToDictionary(a => a.Id.ToString(), a => a.CallSign),
            relations.ToDictionary(r => r.Id.ToString(), r => otherNumbers.GetValueOrDefault(r.Other, "another incident")));

        var entityIds = new List<string> { incidentId.ToString() };
        entityIds.AddRange(context.Reports.Keys);
        entityIds.AddRange(analysisIds.Select(id => id.ToString()));
        entityIds.AddRange(context.Assignments.Keys);
        entityIds.AddRange(context.Relations.Keys);

        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId != null && entityIds.Contains(a.EntityId))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var updates = await db.IncidentUpdates.AsNoTracking().Where(u => u.IncidentId == incidentId).ToListAsync(cancellationToken);

        var userIds = entries.Select(e => e.ActorUserId).OfType<Guid>().Concat(updates.Select(u => u.AuthorId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        UserRef? Ref(Guid? id) => id is { } value && users.TryGetValue(value, out var user) ? user : null;

        // Ties on time are broken by the audit log's own sequence (the order things were recorded);
        // an update sorts after the audit entries written in the same instant.
        var events = entries
            .Where(e => !HiddenFromTimeline.Contains(e.Action))
            .Select(e =>
            {
                var (kind, summary, detail, isAi) = Describe(e, context);
                return (Sequence: e.Id, Event: new TimelineEvent($"audit:{e.Id}", e.OccurredAt, kind, e.ActorType, Ref(e.ActorUserId), e.Action, summary, detail, isAi));
            })
            .Concat(updates.Select(u => (Sequence: long.MaxValue, Event: new TimelineEvent(
                $"update:{u.Id}", u.CreatedAt, TimelineEventKind.Update, AuditActorType.User, Ref(u.AuthorId), $"update.{u.Kind}",
                u.Kind == IncidentUpdateKind.ResolutionNote ? "Resolution notes from the responder" : "Progress update from the responder",
                u.Body, false))))
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToList();
        return events;
    }

    public async Task<Result<PagedResult<AuditEntryResponse>>> SearchAsync(AuditSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var validation = await searchValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var page = new PageRequest(query.Page, Math.Min(query.PageSize, PageRequest.MaxPageSize));
        var entries = db.AuditLogs.AsNoTracking();
        if (query.ActorUserId.HasValue)
        {
            entries = entries.Where(a => a.ActorUserId == query.ActorUserId);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            var type = query.EntityType.Trim();
            entries = entries.Where(a => a.EntityType == type);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            var id = query.EntityId.Trim();
            entries = entries.Where(a => a.EntityId == id);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = entries.Where(a => a.Action == action);
        }

        if (query.From.HasValue)
        {
            var from = query.From.Value.ToUniversalTime();
            entries = entries.Where(a => a.OccurredAt >= from);
        }

        if (query.To.HasValue)
        {
            var to = query.To.Value.ToUniversalTime();
            entries = entries.Where(a => a.OccurredAt <= to);
        }

        var total = await entries.CountAsync(cancellationToken);
        var rows = await entries.OrderByDescending(a => a.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        var userIds = rows.Select(r => r.ActorUserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);

        var items = rows.Select(r => new AuditEntryResponse(
            r.Id, r.OccurredAt, r.ActorType, r.ActorUserId is { } id && users.TryGetValue(id, out var user) ? user : null,
            r.Action, r.EntityType, r.EntityId, r.PreviousValue, r.NewValue, r.Metadata, r.CorrelationId)).ToList();
        return new PagedResult<AuditEntryResponse>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<AuditFacets> FacetsAsync(CancellationToken cancellationToken)
    {
        var entityTypes = await db.AuditLogs.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
        return new AuditFacets(KnownActions, entityTypes);
    }

    private sealed record TimelineContext(
        Dictionary<string, string> Reports,
        Dictionary<string, string> Assignments,
        Dictionary<string, string> Relations);

    /// <summary>A plain-language sentence for each audited action, read from the stored before/after JSON.</summary>
    private static (TimelineEventKind Kind, string Summary, string? Detail, bool IsAi) Describe(AuditLog entry, TimelineContext context)
    {
        using var previous = Parse(entry.PreviousValue);
        using var next = Parse(entry.NewValue);
        using var metadata = Parse(entry.Metadata);
        string? Prev(string name) => Get(previous, name);
        string? New(string name) => Get(next, name);
        string? Meta(string name) => Get(metadata, name);
        var callSign = context.Assignments.GetValueOrDefault(entry.EntityId ?? string.Empty, "A responder");
        var otherIncident = context.Relations.GetValueOrDefault(entry.EntityId ?? string.Empty, "another incident");

        switch (entry.Action)
        {
            case AuditActions.ReportSubmitted:
                var attachments = int.TryParse(New("attachments"), out var count) && count > 0 ? $" with {count} attachment{(count == 1 ? "" : "s")}" : string.Empty;
                return (TimelineEventKind.Report, $"Public report {New("reference")} received{attachments}", null, false);
            case AuditActions.IncidentCreated:
                return (TimelineEventKind.Report, $"Incident {New("number")} opened from the public report", null, false);
            case AuditActions.ReportReassociated:
                var reference = context.Reports.TryGetValue(entry.EntityId ?? string.Empty, out var r) ? $"Report {r}" : "A report";
                return (TimelineEventKind.Correlation, $"{reference} moved after a confirmed duplicate", "The original report is kept unchanged.", false);
            case AuditActions.AiAnalysisQueued:
                return (TimelineEventKind.Ai, $"AI analysis queued (attempt {New("attempt")})", null, true);
            case AuditActions.AiAnalysisCompleted:
                return (TimelineEventKind.Ai, $"AI recommended {New("recommendedSeverity")} severity and category {New("recommendedCategory")}",
                    New("simulated") == "True" ? "Simulated output, not a real AI result." : "A recommendation only; approved values are set by a coordinator.", true);
            case AuditActions.AiAnalysisFailed:
                return (TimelineEventKind.Ai, $"AI analysis failed ({New("failureCode")})", "The report and incident were not changed.", true);
            case AuditActions.AiAnalysisRetryRequested:
                return (TimelineEventKind.Decision, $"AI analysis retry requested (attempt {New("attempt")})", null, false);
            case AuditActions.IncidentStatusChanged:
                var transition = Meta("transition");
                if (entry.ActorType == AuditActorType.System)
                {
                    return (TimelineEventKind.System, $"Status changed automatically from {Words(Prev("status"))} to {Words(New("status"))}", SystemReason(transition), false);
                }

                return (TimelineEventKind.Decision, $"Status changed from {Words(Prev("status"))} to {Words(New("status"))}", DecisionDetail(metadata), false);
            case AuditActions.IncidentUpdated:
                return (TimelineEventKind.Decision, "Incident details edited", ChangedFields(previous, next), false);
            case AuditActions.IncidentSeverityChanged:
                return (TimelineEventKind.Decision, $"Approved severity changed from {Prev("approvedSeverity") ?? "none"} to {New("approvedSeverity")}", null, false);
            case AuditActions.LocationGeocoded:
                return (TimelineEventKind.Location, $"Approximate location found by geocoding ({Words(New("precision"))?.ToLowerInvariant()} precision)",
                    New("placeName") is { } place ? $"Matched as “{place}”. Unverified until a coordinator confirms it." : "Unverified until a coordinator confirms it.", false);
            case AuditActions.LocationGeocodingFailed:
                return (TimelineEventKind.Location, "Location could not be found automatically", New("reason"), false);
            case AuditActions.LocationVerified:
                return (TimelineEventKind.Decision, "Location verified", New("text"), false);
            case AuditActions.RelationSuggested:
                var score = decimal.TryParse(New("score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? $" ({Math.Round(s * 100)}% match)" : string.Empty;
                return (TimelineEventKind.Correlation, $"Possible duplicate of {otherIncident} suggested{score}", New("explanation"), false);
            case AuditActions.RelationLinked:
                return (TimelineEventKind.Correlation, $"Linked as related to {otherIncident}", null, false);
            case AuditActions.RelationDecided:
                return (TimelineEventKind.Correlation, $"Relation with {otherIncident} marked {RelationWords(New("type"))}", null, false);
            case AuditActions.AssignmentCreated:
                return (TimelineEventKind.Assignment, $"{New("callSign") ?? callSign} assigned",
                    Meta("overrideReason") is { } reason ? $"Assigned with override: {reason}" : null, false);
            case AuditActions.AssignmentStatusChanged:
                return (TimelineEventKind.Assignment, $"{callSign}: {Words(Prev("status"))} → {Words(New("status"))}", New("reason"), false);
            default:
                return (TimelineEventKind.System, entry.Action, null, false);
        }
    }

    private static string? SystemReason(string? transition) => transition switch
    {
        nameof(IncidentTransition.FirstAssignmentCreated) => "Because a responder was assigned.",
        nameof(IncidentTransition.ResponderEnRoute) => "Because an assigned responder reported they are on the way.",
        nameof(IncidentTransition.AllAssignmentsEnded) => "Because every assignment ended without completion.",
        _ => null,
    };

    private static string? DecisionDetail(JsonDocument? metadata)
    {
        if (metadata is null || !metadata.RootElement.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var (name, label) in new[] { ("approvedSeverity", "Approved severity"), ("reason", "Reason"), ("resolutionSummary", "Resolution"), ("primaryNumber", "Duplicate of") })
        {
            if (details.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return $"{label}: {value.GetString()}";
            }
        }

        return null;
    }

    private static string? ChangedFields(JsonDocument? previous, JsonDocument? next)
    {
        if (previous is null || next is null)
        {
            return null;
        }

        var changed = next.RootElement.EnumerateObject()
            .Where(p => !previous.RootElement.TryGetProperty(p.Name, out var before) || before.ToString() != p.Value.ToString())
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();
        return changed.Count == 0 ? null : $"Changed: {string.Join(", ", changed)}. The original public report is unchanged.";
    }

    private static string RelationWords(string? type) => type switch
    {
        nameof(RelationType.ConfirmedDuplicate) => "a confirmed duplicate",
        nameof(RelationType.Unrelated) => "not related",
        nameof(RelationType.Related) => "related",
        _ => Words(type) ?? "updated",
    };

    /// <summary>"UnderReview" → "Under review", "EnRoute" → "En route".</summary>
    private static string? Words(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var spaced = string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return spaced;
    }

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Get(JsonDocument? document, string name)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => property.Value.GetRawText(),
                };
            }
        }

        return null;
    }
}
