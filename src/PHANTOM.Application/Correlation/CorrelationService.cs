using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Correlation;

public interface ICorrelationService
{
    /// <summary>
    /// Compares one incident with recent open incidents and records <c>PossibleDuplicate</c> suggestions
    /// (Requirement 7.1–7.3). Never deletes, hides, merges or changes the status of anything (7.4).
    /// Returns the number of new suggestions.
    /// </summary>
    Task<int> CorrelateAsync(Guid incidentId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<RelationResponse>>> ListAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>Manually links two incidents as Related without closing either (7.7).</summary>
    Task<Result<RelationResponse>> LinkAsync(Guid incidentId, LinkIncidentRequest request, CancellationToken cancellationToken);

    /// <summary>Confirm duplicate (7.5), mark unrelated (7.6) or related.</summary>
    Task<Result<RelationDecisionResponse>> DecideAsync(Guid relationId, RelationDecisionRequest request, CancellationToken cancellationToken);
}

internal sealed class CorrelationService(
    IPhantomDbContext db,
    IOptions<CorrelationOptions> options,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock,
    IValidator<LinkIncidentRequest> linkValidator,
    IValidator<RelationDecisionRequest> decisionValidator,
    ILogger<CorrelationService> logger) : ICorrelationService
{
    private const double MetersPerDegreeLatitude = 111_320;
    private const int MaxRowsInWindow = 1000;
    private static readonly JsonSerializerOptions JsonOptions = IntelligenceResultParser.JsonOptions;
    private static readonly Error IncidentNotFound = Error.NotFound("incident.not_found", "Incident not found.");

    /// <summary>Member-initialised (not a positional record) so EF can translate filters applied after the projection.</summary>
    private sealed class CandidateRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public IncidentCategory Category { get; init; }
        public IncidentStatus Status { get; init; }
        public decimal? Latitude { get; init; }
        public decimal? Longitude { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string LocationText { get; init; } = string.Empty;
    }

    public async Task<int> CorrelateAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var settings = options.Value.ToSettings();
        var subject = await Rows().FirstOrDefaultAsync(r => r.Id == incidentId, cancellationToken);
        if (subject is null || IncidentStatusPolicy.IsTerminal(subject.Status))
        {
            return 0;
        }

        var from = subject.CreatedAt - settings.Window;
        var to = subject.CreatedAt + settings.Window;
        var query = Rows().Where(r => r.Id != incidentId && r.CreatedAt >= from && r.CreatedAt <= to
            && r.Status != IncidentStatus.Closed && r.Status != IncidentStatus.Rejected);

        // Bounding-box pre-filter (design §6). Incidents without coordinates are still compared on other signals.
        if (subject.Latitude is { } lat && subject.Longitude is { } lon)
        {
            var dLat = (decimal)(settings.GeoRadiusMeters / MetersPerDegreeLatitude);
            var dLon = (decimal)(settings.GeoRadiusMeters / (MetersPerDegreeLatitude * Math.Max(0.01, Math.Cos((double)lat * Math.PI / 180))));
            query = query.Where(r => r.Latitude == null || r.Longitude == null
                || (r.Latitude >= lat - dLat && r.Latitude <= lat + dLat && r.Longitude >= lon - dLon && r.Longitude <= lon + dLon));
        }

        // Pairs already related in any way (including "unrelated" decisions) are never re-suggested (7.6).
        var alreadyRelated = await db.IncidentRelations.AsNoTracking()
            .Where(r => r.SourceIncidentId == incidentId || r.TargetIncidentId == incidentId)
            .Select(r => r.SourceIncidentId == incidentId ? r.TargetIncidentId : r.SourceIncidentId)
            .ToListAsync(cancellationToken);

        var candidates = (await query.Where(r => !alreadyRelated.Contains(r.Id)).Take(MaxRowsInWindow).ToListAsync(cancellationToken))
            .OrderBy(r => Math.Abs((r.CreatedAt - subject.CreatedAt).Ticks)).ThenBy(r => r.Id)
            .Take(settings.MaxCandidates)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var places = await NamedPlacesAsync([incidentId, .. candidates.Select(c => c.Id)], cancellationToken);
        var subjectFeatures = Features(subject, places);
        var now = clock.UtcNow;
        var suggestions = 0;
        foreach (var candidate in candidates)
        {
            var result = CorrelationScorer.Score(subjectFeatures, Features(candidate, places), settings);
            if (result.Score < settings.SuggestThreshold)
            {
                continue;
            }

            var relation = IncidentRelation.Suggest(Guid.CreateVersion7(), incidentId, candidate.Id, (decimal)result.Score,
                JsonSerializer.Serialize(CorrelationSignals.From(result), JsonOptions), result.Explanation, now);
            db.IncidentRelations.Add(relation);
            audit.RecordSystem(AuditActions.RelationSuggested, nameof(IncidentRelation), relation.Id.ToString(),
                newValue: new { relation.SourceIncidentId, relation.TargetIncidentId, type = relation.Type.ToString(), relation.Score, relation.Explanation });
            suggestions++;
        }

        if (suggestions == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent run (e.g. the other incident's own correlation) recorded the same pair first.
            logger.LogInformation(ex, "Correlation for incident {IncidentId} raced with another run; suggestions skipped", incidentId);
            return 0;
        }

        logger.LogInformation("Correlation suggested {Count} possible duplicates for incident {IncidentId}", suggestions, incidentId);
        return suggestions;
    }

    public async Task<Result<IReadOnlyList<RelationResponse>>> ListAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, cancellationToken))
        {
            return IncidentNotFound;
        }

        var relations = await db.IncidentRelations.AsNoTracking()
            .Where(r => r.SourceIncidentId == incidentId || r.TargetIncidentId == incidentId)
            .ToListAsync(cancellationToken);
        return await ToResponsesAsync(incidentId, relations, cancellationToken);
    }

    public async Task<Result<RelationResponse>> LinkAsync(Guid incidentId, LinkIncidentRequest request, CancellationToken cancellationToken)
    {
        var validation = await linkValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, cancellationToken))
        {
            return IncidentNotFound;
        }

        var number = request.OtherIncidentNumber.Trim().ToUpperInvariant();
        var otherId = await db.Incidents.Where(i => i.Number == number).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(cancellationToken);
        if (otherId is null)
        {
            return Error.NotFound("incident.not_found", $"No incident {number} exists.");
        }

        if (otherId == incidentId)
        {
            return Error.Validation("relation.self", "An incident cannot be linked to itself.");
        }

        var (source, target) = IncidentRelation.Order(incidentId, otherId.Value);
        var relation = await db.IncidentRelations.FirstOrDefaultAsync(r => r.SourceIncidentId == source && r.TargetIncidentId == target, cancellationToken);
        var now = clock.UtcNow;
        if (relation is null)
        {
            relation = IncidentRelation.LinkManually(Guid.CreateVersion7(), incidentId, otherId.Value, userId, now);
            db.IncidentRelations.Add(relation);
            audit.Record(AuditActions.RelationLinked, nameof(IncidentRelation), relation.Id.ToString(),
                newValue: new { relation.SourceIncidentId, relation.TargetIncidentId, type = relation.Type.ToString() });
        }
        else
        {
            var previous = relation.Type;
            var marked = relation.MarkRelated(userId, now);
            if (marked.IsFailure)
            {
                return Error.Conflict("relation.exists", $"These incidents are already linked ({previous}).");
            }

            audit.Record(AuditActions.RelationDecided, nameof(IncidentRelation), relation.Id.ToString(),
                new { type = previous.ToString() }, new { type = relation.Type.ToString() });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("relation.exists", "These incidents were linked by someone else just now. Reload to see it.");
        }

        var responses = await ToResponsesAsync(incidentId, [relation], cancellationToken);
        return responses[0];
    }

    public async Task<Result<RelationDecisionResponse>> DecideAsync(Guid relationId, RelationDecisionRequest request, CancellationToken cancellationToken)
    {
        var validation = await decisionValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        var relation = await db.IncidentRelations.FirstOrDefaultAsync(r => r.Id == relationId, cancellationToken);
        if (relation is null)
        {
            return Error.NotFound("relation.not_found", "Relation not found.");
        }

        var previous = relation.Type;
        var now = clock.UtcNow;
        Result outcome = request.Decision switch
        {
            RelationDecision.Related => relation.MarkRelated(userId, now),
            RelationDecision.Unrelated => relation.MarkUnrelated(userId, now),
            _ => await ConfirmDuplicateAsync(relation, request.PrimaryIncidentId!.Value, userId, now, cancellationToken),
        };
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        audit.Record(AuditActions.RelationDecided, nameof(IncidentRelation), relation.Id.ToString(),
            new { type = previous.ToString() },
            new { type = relation.Type.ToString(), relation.PrimaryIncidentId });
        await db.SaveChangesAsync(cancellationToken);
        return new RelationDecisionResponse(relation.Id, relation.Type, relation.PrimaryIncidentId, relation.DecidedAt);
    }

    /// <summary>
    /// Requirement 7.5: the secondary's reports are associated with the primary (each audited), and the
    /// secondary is closed as a duplicate. Nothing is deleted; both incidents and every report stay readable.
    /// </summary>
    private async Task<Result> ConfirmDuplicateAsync(IncidentRelation relation, Guid primaryId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!relation.Involves(primaryId))
        {
            return Error.Validation("relation.primary_invalid", "The primary incident must be one of the pair.");
        }

        var secondaryId = relation.Other(primaryId);
        var incidents = await db.Incidents.Where(i => i.Id == primaryId || i.Id == secondaryId).ToListAsync(cancellationToken);
        var primary = incidents.Single(i => i.Id == primaryId);
        var secondary = incidents.Single(i => i.Id == secondaryId);
        if (IncidentStatusPolicy.IsTerminal(primary.Status))
        {
            return Error.InvalidTransition("relation.primary_closed", $"{primary.Number} is {primary.Status}; choose an open incident as the primary.");
        }

        var secondaryStatus = secondary.Status;
        var closed = secondary.CloseAsDuplicate(primary.Number, now);
        if (closed.IsFailure)
        {
            return Error.InvalidTransition("relation.secondary_not_closable",
                $"{secondary.Number} is {secondaryStatus}; only a Reported or Under review incident can be closed as a duplicate.");
        }

        var decided = relation.ConfirmDuplicate(primaryId, userId, now);
        if (decided.IsFailure)
        {
            return decided;
        }

        audit.Record(AuditActions.IncidentStatusChanged, nameof(Incident), secondary.Id.ToString(),
            new { status = secondaryStatus.ToString() },
            new { status = secondary.Status.ToString() },
            new { transition = nameof(IncidentTransition.CloseAsDuplicate), details = new { primaryIncidentId = primary.Id, primaryNumber = primary.Number, relationId = relation.Id } });

        var reports = await db.IncidentReports.Where(r => r.IncidentId == secondaryId).ToListAsync(cancellationToken);
        foreach (var report in reports)
        {
            report.ReassignTo(primaryId);
            audit.Record(AuditActions.ReportReassociated, nameof(IncidentReport), report.Id.ToString(),
                new { incidentId = secondaryId }, new { incidentId = primaryId }, new { relationId = relation.Id, originalIncidentId = report.OriginalIncidentId });
        }

        return Result.Success();
    }

    private IQueryable<CandidateRow> Rows() =>
        db.Incidents.AsNoTracking().Select(i => new CandidateRow
        {
            Id = i.Id,
            CreatedAt = i.CreatedAt,
            Category = i.Category,
            Status = i.Status,
            Latitude = i.Location.Latitude,
            Longitude = i.Location.Longitude,
            Title = i.Title,
            Description = i.Description,
            LocationText = i.Location.Text,
        });

    /// <summary>Place names from each incident's latest successful AI analysis.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> NamedPlacesAsync(List<Guid> incidentIds, CancellationToken cancellationToken)
    {
        var rows = await db.IncidentAnalyses.AsNoTracking()
            .Where(a => incidentIds.Contains(a.IncidentId) && a.Status == AnalysisStatus.Succeeded && a.Result != null)
            .Select(a => new { a.IncidentId, a.CompletedAt, a.Result })
            .ToListAsync(cancellationToken);

        var places = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var latest in rows.GroupBy(r => r.IncidentId).Select(g => g.OrderByDescending(r => r.CompletedAt).First()))
        {
            var parsed = IntelligenceResultParser.Parse(latest.Result);
            if (parsed.IsSuccess)
            {
                places[latest.IncidentId] = parsed.Value.NamedLocations;
            }
        }

        return places;
    }

    private static IncidentFeatures Features(CandidateRow row, Dictionary<Guid, IReadOnlyList<string>> places) =>
        new(row.Id, row.CreatedAt, row.Category, (double?)row.Latitude, (double?)row.Longitude, $"{row.Title} {row.Description}",
            [row.LocationText, .. places.GetValueOrDefault(row.Id, [])]);

    private async Task<List<RelationResponse>> ToResponsesAsync(Guid incidentId, List<IncidentRelation> relations, CancellationToken cancellationToken)
    {
        var otherIds = relations.Select(r => r.Other(incidentId)).Distinct().ToList();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == incidentId || otherIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Number, i.Title, i.Status, i.Category, i.ApprovedSeverity, LocationText = i.Location.Text, i.CreatedAt })
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        var userIds = relations.SelectMany(r => new[] { r.CreatedById, r.DecidedById }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        UserRef? Ref(Guid? id) => id is { } value && users.TryGetValue(value, out var user) ? user : null;
        var isCoordinator = currentUser.IsInRole(RoleNames.IncidentCoordinator);

        return relations
            .OrderBy(r => r.Type == RelationType.PossibleDuplicate ? 0 : 1).ThenByDescending(r => r.Score).ThenByDescending(r => r.CreatedAt)
            .Select(r =>
            {
                var other = incidents[r.Other(incidentId)];
                var self = incidents[incidentId];
                var primaries = new List<Guid>();
                if (CanBePrimary(self.Status, other.Status)) primaries.Add(incidentId);
                if (CanBePrimary(other.Status, self.Status)) primaries.Add(other.Id);
                List<RelationDecision> decisions = !isCoordinator ? [] : r.Type switch
                {
                    RelationType.PossibleDuplicate => [RelationDecision.Related, RelationDecision.Unrelated],
                    RelationType.Related => [RelationDecision.Unrelated],
                    RelationType.Unrelated => [RelationDecision.Related],
                    _ => [],
                };
                if (isCoordinator && r.Type != RelationType.ConfirmedDuplicate && primaries.Count > 0)
                {
                    decisions.Insert(0, RelationDecision.ConfirmDuplicate);
                }

                CorrelationSignals? signals = null;
                if (r.Signals is not null)
                {
                    try
                    {
                        signals = JsonSerializer.Deserialize<CorrelationSignals>(r.Signals, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "Unreadable signals on relation {RelationId}", r.Id);
                    }
                }

                return new RelationResponse(r.Id, r.Type, r.Score, r.Explanation, signals,
                    new RelatedIncidentSummary(other.Id, other.Number, other.Title, other.Status, other.Category, other.ApprovedSeverity, other.LocationText, other.CreatedAt),
                    r.PrimaryIncidentId, Ref(r.CreatedById), r.CreatedAt, Ref(r.DecidedById), r.DecidedAt,
                    isCoordinator ? decisions : [], isCoordinator && r.Type != RelationType.ConfirmedDuplicate ? primaries : []);
            })
            .ToList();
    }

    /// <summary>A primary must stay open, and the other incident must still be closable as a duplicate.</summary>
    private static bool CanBePrimary(IncidentStatus primary, IncidentStatus secondary) =>
        !IncidentStatusPolicy.IsTerminal(primary)
        && IncidentStatusPolicy.TryGetRule(secondary, IncidentTransition.CloseAsDuplicate, out _);
}
