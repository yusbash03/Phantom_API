using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Correlation;
using PHANTOM.Application.Geocoding;
using PHANTOM.Application.Incidents;
using PHANTOM.Application.Notifications;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Notifications;

namespace PHANTOM.Application.Intelligence;

public interface IAnalysisService
{
    /// <summary>Runs one claimed (Processing) analysis to completion. Called by the background worker.</summary>
    Task ProcessAsync(Guid analysisId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<AnalysisResponse>>> ListAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>Queues a new attempt after the latest one failed (Requirement 6.5).</summary>
    Task<Result<AnalysisResponse>> RetryAsync(Guid incidentId, CancellationToken cancellationToken);
}

internal sealed class AnalysisService(
    IPhantomDbContext db,
    IIncidentIntelligenceService intelligence,
    IAnalysisSignal signal,
    ICorrelationService correlation,
    IGeocodingSignal geocodingSignal,
    INotifier notifier,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IObjectStorage storage,
    ITranscriptionService transcription,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    /// <summary>
    /// Photo limits per analysis (Requirement 6.10): enough to help, small enough for an inline request.
    /// Stored photos are already re-encoded and at most 10 MB each.
    /// </summary>
    public const int MaxImages = 3;
    public const long MaxImageBytesTotal = 12 * 1024 * 1024;

    private const int MaxConcurrencyRetries = 3;

    public async Task ProcessAsync(Guid analysisId, CancellationToken cancellationToken)
    {
        var analysis = await db.IncidentAnalyses.FirstOrDefaultAsync(a => a.Id == analysisId, cancellationToken);
        if (analysis is null || analysis.Status != AnalysisStatus.Processing)
        {
            logger.LogWarning("Analysis {AnalysisId} is not claimed for processing; skipping", analysisId);
            return;
        }

        var report = await db.IncidentReports.AsNoTracking()
            .Where(r => r.Id == analysis.ReportId)
            .Select(r => new IntelligenceRequest(r.Title, r.Description, r.Category, r.LocationText, r.SubmittedAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (report is not null)
        {
            var transcript = await EnsureTranscriptAsync(analysis.ReportId, cancellationToken);
            report = report with
            {
                Images = await LoadImagesAsync(analysis.ReportId, cancellationToken),
                AudioTranscript = transcript is null ? null : TranscriptRedaction.Redact(transcript),
            };
        }

        var started = Stopwatch.GetTimestamp();
        IntelligenceOutcome outcome;
        if (report is null)
        {
            outcome = IntelligenceOutcome.Failure("none", "none", AnalysisFailureCode.ProviderError, "The input report could not be found.");
        }
        else
        {
            try
            {
                outcome = await intelligence.AnalyzeAsync(report, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A provider adapter bug must not leave the attempt stuck; the report stays intact either way.
                logger.LogError(ex, "Intelligence provider threw for analysis {AnalysisId}", analysisId);
                outcome = IntelligenceOutcome.Failure("unknown", "unknown", AnalysisFailureCode.ProviderError, "The AI provider call failed unexpectedly.");
            }
        }

        var latencyMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var now = clock.UtcNow;
        var parsed = outcome.IsSuccess ? IntelligenceResultParser.Parse(outcome.RawJson) : null;

        if (parsed is { IsSuccess: true })
        {
            var result = parsed.Value;
            var imagesUsed = Math.Min(outcome.ImagesUsed, report?.Images.Count ?? 0);
            var transcriptUsed = outcome.TranscriptUsed && !string.IsNullOrWhiteSpace(report?.AudioTranscript);
            analysis.Succeed(outcome.Provider, outcome.Model, IntelligenceResultParser.Serialize(result), result.Severity, result.Category, latencyMs, now,
                usedImageInput: imagesUsed > 0, usedAudioTranscript: transcriptUsed);
            audit.RecordSystem(AuditActions.AiAnalysisCompleted, nameof(IncidentAnalysis), analysis.Id.ToString(),
                newValue: new
                {
                    incidentId = analysis.IncidentId,
                    attempt = analysis.Attempt,
                    recommendedSeverity = result.Severity.ToString(),
                    recommendedCategory = result.Category.ToString(),
                    provider = outcome.Provider,
                    model = outcome.Model,
                    simulated = IntelligenceProviders.IsStandIn(outcome.Provider),
                    latencyMs,
                    imagesUsed,
                    imageAttachmentIds = imagesUsed > 0 ? report!.Images.Take(imagesUsed).Select(i => i.AttachmentId).ToArray() : null,
                    transcriptUsed,
                });
            // Requirement 14.1: a newly Critical recommendation alerts coordinators (once, not on every retry).
            if (result.Severity == Severity.Critical)
            {
                var incident = await db.Incidents.AsNoTracking().Where(i => i.Id == analysis.IncidentId)
                    .Select(i => new { i.Number, i.Title, i.AiRecommendedSeverity }).FirstOrDefaultAsync(cancellationToken);
                if (incident is not null && incident.AiRecommendedSeverity != Severity.Critical)
                {
                    await notifier.NotifyCoordinatorsAsync(NotificationType.CriticalAiRecommendation, $"AI recommends Critical for {incident.Number}",
                        $"{incident.Title}. This is an AI recommendation, not a decision: review and verify the severity.", analysis.IncidentId, null, cancellationToken);
                }
            }

            await SaveWithRecommendationAsync(analysis.IncidentId, result, now, cancellationToken);

            if (result.NamedLocations.Count > 0)
            {
                await RequeueGeocodingAsync(analysis.IncidentId, cancellationToken);
            }

            // AI-extracted place names are a correlation signal, so compare again (Requirement 7.1).
            try
            {
                await correlation.CorrelateAsync(analysis.IncidentId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Correlation after analysis {AnalysisId} failed; the analysis result is kept", analysis.Id);
            }

            logger.LogInformation("Analysis {AnalysisId} for incident {IncidentId} succeeded in {LatencyMs} ms ({Provider}/{Model})",
                analysis.Id, analysis.IncidentId, latencyMs, outcome.Provider, outcome.Model);
            return;
        }

        var code = outcome.FailureCode ?? AnalysisFailureCode.InvalidOutput;
        var message = outcome.FailureMessage ?? parsed?.Error?.Message ?? "The AI response was not usable.";
        analysis.Fail(code, message, outcome.Provider, outcome.Model, latencyMs, now);
        audit.RecordSystem(AuditActions.AiAnalysisFailed, nameof(IncidentAnalysis), analysis.Id.ToString(),
            newValue: new { incidentId = analysis.IncidentId, attempt = analysis.Attempt, failureCode = code.ToString(), provider = outcome.Provider, model = outcome.Model, latencyMs });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Analysis {AnalysisId} for incident {IncidentId} failed: {FailureCode}", analysis.Id, analysis.IncidentId, code);
    }

    /// <summary>
    /// The voice recording's transcript (Requirement 6.11). Transcribes on first use when a provider is configured
    /// and stores the result on the attachment, beside the untouched original. A failure is audited and the
    /// analysis continues on the text alone; the next attempt tries again.
    /// </summary>
    private async Task<string?> EnsureTranscriptAsync(Guid reportId, CancellationToken cancellationToken)
    {
        var recording = await db.Attachments
            .Where(a => a.ReportId == reportId && a.Kind == AttachmentKind.Audio)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (recording is null || recording.TranscriptText is not null || !transcription.IsConfigured)
        {
            return recording?.TranscriptText;
        }

        byte[] audio;
        try
        {
            await using var stream = await storage.RetrieveAsync(recording.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            audio = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Voice recording {AttachmentId} could not be read for transcription", recording.Id);
            return null;
        }

        var outcome = await transcription.TranscribeAsync(recording.ContentType, audio, cancellationToken);
        if (outcome.IsSuccess && recording.SetTranscript(outcome.Text!, outcome.Provider, clock.UtcNow))
        {
            audit.RecordSystem(AuditActions.AttachmentTranscribed, nameof(Attachment), recording.Id.ToString(),
                newValue: new { reportId, provider = outcome.Provider, characters = recording.TranscriptText!.Length });
        }
        else if (!outcome.IsSuccess)
        {
            audit.RecordSystem(AuditActions.AttachmentTranscriptionFailed, nameof(Attachment), recording.Id.ToString(),
                newValue: new { reportId, provider = outcome.Provider, reason = outcome.FailureMessage });
        }

        await db.SaveChangesAsync(cancellationToken);
        return recording.TranscriptText;
    }

    /// <summary>
    /// The report's photos, oldest first, within <see cref="MaxImages"/> and <see cref="MaxImageBytesTotal"/>.
    /// A photo that cannot be read is skipped: the text analysis still runs.
    /// </summary>
    private async Task<IReadOnlyList<IntelligenceImage>> LoadImagesAsync(Guid reportId, CancellationToken cancellationToken)
    {
        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.ReportId == reportId && a.Kind == AttachmentKind.Image)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.StorageKey, a.ContentType, a.SizeBytes })
            .ToListAsync(cancellationToken);

        var images = new List<IntelligenceImage>();
        long total = 0;
        foreach (var a in attachments)
        {
            if (images.Count == MaxImages || total + a.SizeBytes > MaxImageBytesTotal)
            {
                break;
            }

            try
            {
                await using var stream = await storage.RetrieveAsync(a.StorageKey, cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                images.Add(new IntelligenceImage(a.Id, a.ContentType, buffer.ToArray()));
                total += buffer.Length;
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Photo {AttachmentId} could not be read for analysis; continuing without it", a.Id);
            }
        }

        return images;
    }

    public async Task<Result<IReadOnlyList<AnalysisResponse>>> ListAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, cancellationToken))
        {
            return Error.NotFound("incident.not_found", "Incident not found.");
        }

        var analyses = await db.IncidentAnalyses.AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .OrderByDescending(a => a.Attempt).ThenByDescending(a => a.RequestedAt)
            .ToListAsync(cancellationToken);
        var reportIds = analyses.Select(a => a.ReportId).Distinct().ToList();
        var references = await db.IncidentReports.AsNoTracking()
            .Where(r => reportIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Reference, cancellationToken);
        var requesterIds = analyses.Select(a => a.RequestedById).OfType<Guid>().Distinct().ToList();
        var requesters = await db.Users.AsNoTracking()
            .Where(u => requesterIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);

        return analyses.Select(a => ToResponse(a, references.GetValueOrDefault(a.ReportId, string.Empty),
            a.RequestedById is { } id && requesters.TryGetValue(id, out var user) ? user : null)).ToList();
    }

    public async Task<Result<AnalysisResponse>> RetryAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, cancellationToken))
        {
            return Error.NotFound("incident.not_found", "Incident not found.");
        }

        var latest = await db.IncidentAnalyses.AsNoTracking()
            .Where(a => a.IncidentId == incidentId)
            .OrderByDescending(a => a.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return Error.NotFound("analysis.not_found", "This incident has no analysis to retry.");
        }

        if (latest.Status != AnalysisStatus.Failed)
        {
            return Error.Conflict("analysis.not_failed", latest.IsFinished
                ? "The latest analysis succeeded; only a failed analysis can be retried."
                : "An analysis is already queued or running for this incident.");
        }

        var nextAttempt = await db.IncidentAnalyses.Where(a => a.ReportId == latest.ReportId).MaxAsync(a => a.Attempt, cancellationToken) + 1;
        var retry = IncidentAnalysis.CreatePending(Guid.CreateVersion7(), incidentId, latest.ReportId, nextAttempt, clock.UtcNow, userId);
        db.IncidentAnalyses.Add(retry);
        audit.Record(AuditActions.AiAnalysisRetryRequested, nameof(IncidentAnalysis), retry.Id.ToString(),
            previousValue: new { analysisId = latest.Id, attempt = latest.Attempt, failureCode = latest.FailureCode?.ToString() },
            newValue: new { incidentId, attempt = nextAttempt });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Unique (report_id, attempt): someone else queued the retry first.
            return Error.Conflict("analysis.retry_in_progress", "A retry was already requested for this incident.");
        }

        signal.Notify();
        var reference = await db.IncidentReports.Where(r => r.Id == retry.ReportId).Select(r => r.Reference).FirstAsync(cancellationToken);
        var requester = await db.Users.Where(u => u.Id == userId).Select(u => new UserRef(u.Id, u.DisplayName)).FirstOrDefaultAsync(cancellationToken);
        return ToResponse(retry, reference, requester);
    }

    /// <summary>
    /// When the location text could not be geocoded, the AI-extracted place names give it another chance
    /// (design §5). One conditional UPDATE, mirroring <see cref="Incident.RequestGeocodingRetry"/>: it
    /// re-reads nothing from the change tracker, so it can neither act on a stale status nor overwrite a
    /// geocode that finished in the meantime.
    /// </summary>
    private async Task RequeueGeocodingAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var requeued = await db.Incidents
            .Where(i => i.Id == incidentId
                && i.Location.Latitude == null
                && i.Location.Provenance != LocationProvenance.ManuallyVerified
                && (i.GeocodingStatus == GeocodingStatus.NoResult || i.GeocodingStatus == GeocodingStatus.Failed || i.GeocodingStatus == GeocodingStatus.Processing))
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.GeocodingStatus, GeocodingStatus.Pending)
                .SetProperty(i => i.GeocodingGeneration, i => i.GeocodingGeneration + 1), cancellationToken);
        if (requeued > 0)
        {
            geocodingSignal.Notify();
        }
    }

    /// <summary>
    /// Writes the AI recommendation through the domain method, which cannot touch approved values.
    /// A coordinator may save the incident meanwhile; then the incident is reloaded and only the AI
    /// fields are re-applied, so no human change is ever overwritten.
    /// </summary>
    private async Task SaveWithRecommendationAsync(Guid incidentId, IntelligenceResultV1 result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
            incident?.RecordAiRecommendation(result.Severity, result.Category, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxConcurrencyRetries)
            {
                foreach (var entry in ex.Entries)
                {
                    await entry.ReloadAsync(cancellationToken);
                }
            }
        }
    }

    private static AnalysisResponse ToResponse(IncidentAnalysis a, string reportReference, UserRef? requestedBy)
    {
        IntelligenceResultV1? result = null;
        if (a.Result is not null)
        {
            var parsed = IntelligenceResultParser.Parse(a.Result);
            result = parsed.IsSuccess ? parsed.Value : null;
        }

        return new AnalysisResponse(a.Id, a.Attempt, reportReference, a.Status, a.Provider, a.Model, IntelligenceProviders.IsStandIn(a.Provider), a.SchemaVersion,
            a.RequestedAt, a.CompletedAt, a.LatencyMs, a.RecommendedSeverity, a.RecommendedCategory, result, a.FailureCode, a.FailureMessage, requestedBy, a.UsedImageInput, a.UsedAudioTranscript);
    }
}
