using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Application.Correlation;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Reporting.Intake;

public sealed class ReportIntakeService(
    IPhantomDbContext db,
    IValidator<SubmitReportRequest> validator,
    IFileInspector fileInspector,
    IImageProcessor imageProcessor,
    IObjectStorage objectStorage,
    IIncidentNumberGenerator numberGenerator,
    IAuditWriter audit,
    IAnalysisSignal analysisSignal,
    ICorrelationService correlation,
    IClock clock,
    ILogger<ReportIntakeService> logger) : IReportIntakeService
{
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;
    public const int MaxImages = 5;
    public const int MaxAudioFiles = 1;
    public const int MaxFiles = MaxImages + MaxAudioFiles;
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private const string FilesField = "files";
    private const int SniffBytes = 16;
    private static readonly string[] ImageMimeTypes = ["image/jpeg", "image/png", "image/webp"];

    public async Task<Result<SubmitReportResponse>> SubmitAsync(SubmitReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        // Canonical form so "ABC…" and "abc…" are the same key.
        var idempotencyKey = request.IdempotencyKey is null ? null : Guid.Parse(request.IdempotencyKey).ToString("D");
        if (idempotencyKey is not null)
        {
            var replay = await FindReplayAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }
        }

        var locationResult = Location.Reported(request.LocationText, request.Latitude, request.Longitude);
        if (locationResult.IsFailure)
        {
            return locationResult.Error!;
        }

        var processedFiles = new List<ProcessedFile>(request.Files.Count);
        var storedKeys = new List<string>(request.Files.Count);
        try
        {
            var filesResult = await ProcessFilesAsync(request.Files, processedFiles, cancellationToken).ConfigureAwait(false);
            if (filesResult.IsFailure)
            {
                return filesResult.Error!;
            }

            // PostgreSQL stores microseconds; truncate so a replay returns exactly the same timestamp.
            var utcNow = clock.UtcNow;
            var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
            var reference = await numberGenerator.NextAsync(cancellationToken).ConfigureAwait(false);
            var incidentResult = Incident.Create(Guid.CreateVersion7(), reference, request.Title, request.Description,
                request.Category, locationResult.Value, now);
            if (incidentResult.IsFailure)
            {
                return incidentResult.Error!;
            }

            var incident = incidentResult.Value;
            var report = IncidentReport.Create(
                Guid.CreateVersion7(),
                reference,
                incident.Id,
                request.Title,
                request.Description,
                request.Category,
                request.LocationText,
                incident.Location.Latitude,
                incident.Location.Longitude,
                Blank(request.ReporterName),
                Blank(request.ReporterPhone),
                Blank(request.ReporterEmail),
                now,
                idempotencyKey,
                HashClientIp(request.ClientIp));
            var analysis = IncidentAnalysis.CreatePending(Guid.CreateVersion7(), incident.Id, report.Id, attempt: 1, now);

            db.Incidents.Add(incident);
            db.IncidentReports.Add(report);
            db.IncidentAnalyses.Add(analysis);
            foreach (var file in processedFiles)
            {
                db.Attachments.Add(Attachment.Create(Guid.CreateVersion7(), report.Id, file.Kind, file.StorageKey,
                    file.ContentType, file.Data.Length, file.Sha256, file.OriginalFileName, now));
            }

            // Anonymous actor; never put reporter contact details in the audit trail.
            audit.Record(AuditActions.ReportSubmitted, nameof(IncidentReport), report.Id.ToString(),
                newValue: new { reference, category = request.Category.ToString(), attachments = processedFiles.Count, hasCoordinates = incident.Location.HasCoordinates });
            audit.Record(AuditActions.IncidentCreated, nameof(Incident), incident.Id.ToString(),
                newValue: new { number = reference, status = incident.Status.ToString(), category = incident.Category.ToString() });
            audit.RecordSystem(AuditActions.AiAnalysisQueued, nameof(IncidentAnalysis), analysis.Id.ToString(),
                newValue: new { incidentId = incident.Id, attempt = analysis.Attempt });

            // Files go to storage first; if the database write then fails they are removed again.
            foreach (var file in processedFiles)
            {
                await objectStorage.StoreAsync(file.StorageKey, file.Data, file.ContentType, cancellationToken).ConfigureAwait(false);
                storedKeys.Add(file.StorageKey);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException) when (idempotencyKey is not null)
            {
                // A concurrent request with the same key won the unique index; answer with its reference.
                await DeleteStoredAsync(storedKeys).ConfigureAwait(false);
                storedKeys.Clear();
                var replay = await FindReplayAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
                if (replay is not null)
                {
                    return replay;
                }

                throw;
            }

            storedKeys.Clear();
            analysisSignal.Notify();
            await CorrelateBestEffortAsync(incident.Id, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Report {Reference} submitted: incident {IncidentId}, analysis {AnalysisId}, {AttachmentCount} attachments",
                reference, incident.Id, analysis.Id, processedFiles.Count);
            return new SubmitReportResponse(reference, now);
        }
        finally
        {
            // Non-empty only when the submission did not commit.
            await DeleteStoredAsync(storedKeys).ConfigureAwait(false);
            foreach (var file in processedFiles)
            {
                await file.Data.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<Result<PublicStatusResponse>> GetPublicStatusAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > IncidentReport.MaxReferenceLength)
        {
            return Error.NotFound("report.not_found", "Reference not found.");
        }

        var normalised = reference.Trim().ToUpperInvariant();
        var status = await db.IncidentReports
            .AsNoTracking()
            .Where(r => r.Reference == normalised)
            .Join(db.Incidents, r => r.IncidentId, i => i.Id, (_, i) => (IncidentStatus?)i.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return status is null
            ? Error.NotFound("report.not_found", "Reference not found.")
            : new PublicStatusResponse(normalised, ToPublicStatus(status.Value));
    }

    /// <summary>Coarse public status (Requirement 4.10). Internal states never leak to anonymous callers.</summary>
    public static PublicReportStatus ToPublicStatus(IncidentStatus status) => status switch
    {
        IncidentStatus.Reported => PublicReportStatus.Received,
        IncidentStatus.UnderReview or IncidentStatus.Verified => PublicReportStatus.UnderReview,
        IncidentStatus.Assigned or IncidentStatus.InProgress => PublicReportStatus.BeingHandled,
        IncidentStatus.Resolved or IncidentStatus.Closed or IncidentStatus.Rejected => PublicReportStatus.Closed,
        _ => PublicReportStatus.Received,
    };

    /// <summary>The report is already committed; a correlation problem must never fail the submission.</summary>
    private async Task CorrelateBestEffortAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        try
        {
            await correlation.CorrelateAsync(incidentId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Correlation failed for new incident {IncidentId}; the report is unaffected", incidentId);
        }
    }

    private async Task<Result<SubmitReportResponse>?> FindReplayAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await db.IncidentReports
            .AsNoTracking()
            .Where(r => r.IdempotencyKey == idempotencyKey)
            .Select(r => new { r.Reference, r.SubmittedAt })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        if (existing.SubmittedAt < clock.UtcNow - IdempotencyWindow)
        {
            return Error.Conflict("report.idempotency_key_expired", "This Idempotency-Key was already used. Generate a new key for a new report.");
        }

        logger.LogInformation("Idempotent replay of report {Reference}", existing.Reference);
        return new SubmitReportResponse(existing.Reference, existing.SubmittedAt);
    }

    private async Task<Result> ProcessFilesAsync(IReadOnlyList<FileAttachment> files, List<ProcessedFile> processed, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            var displayName = SanitiseFileName(file.OriginalFileName);
            var bytes = await ReadBoundedAsync(file.Stream, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return FileError("report.file_too_large", $"'{displayName}' exceeds the 10 MB limit.");
            }

            // The client's file name and Content-Type are ignored; only the content signature counts.
            var contentType = fileInspector.DetectAllowedMimeType(bytes.AsSpan(0, Math.Min(bytes.Length, SniffBytes)));
            if (contentType is null)
            {
                return FileError("report.file_type_not_allowed",
                    $"'{displayName}' is not an allowed type. Accepted: JPEG, PNG or WebP images; WebM, OGG, MP3, WAV or M4A audio.");
            }

            var kind = ImageMimeTypes.Contains(contentType, StringComparer.Ordinal) ? AttachmentKind.Image : AttachmentKind.Audio;
            MemoryStream data;
            if (kind == AttachmentKind.Image)
            {
                using var original = new MemoryStream(bytes, writable: false);
                var stripped = await imageProcessor.StripMetadataAsync(original, contentType, cancellationToken).ConfigureAwait(false);
                if (stripped is null)
                {
                    return FileError("report.image_unreadable", $"'{displayName}' could not be read as an image, or its dimensions are too large.");
                }

                data = stripped;
            }
            else
            {
                data = new MemoryStream(bytes, writable: false);
            }

            data.Position = 0;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(data, cancellationToken).ConfigureAwait(false));
            data.Position = 0;
            processed.Add(new ProcessedFile(Guid.CreateVersion7().ToString("N"), kind, contentType, sha256, displayName, data));
        }

        if (processed.Count(f => f.Kind == AttachmentKind.Image) > MaxImages)
        {
            return FileError("report.too_many_images", $"At most {MaxImages} images can be attached.");
        }

        if (processed.Count(f => f.Kind == AttachmentKind.Audio) > MaxAudioFiles)
        {
            return FileError("report.too_many_audio_files", $"At most {MaxAudioFiles} audio recording can be attached.");
        }

        return Result.Success();
    }

    /// <summary>Reads at most <see cref="MaxFileSizeBytes"/>; returns null when the stream is larger.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFileSizeBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task DeleteStoredAsync(List<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await objectStorage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not delete orphaned storage object {StorageKey}", key);
            }
        }
    }

    private static Error FileError(string code, string message) =>
        Error.Validation(code, message) with { Fields = new Dictionary<string, string[]> { [FilesField] = [message] } };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? HashClientIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        // For abuse analysis only; the raw address is never stored.
        const string salt = "phantom-ip-hash-salt";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ip)));
    }

    private static string SanitiseFileName(string? name)
    {
        var fileName = Path.GetFileName(name ?? string.Empty);
        if (fileName.Length > Attachment.MaxOriginalFileNameLength)
        {
            fileName = fileName[..Attachment.MaxOriginalFileNameLength];
        }

        var safe = new string(fileName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('_', '.');
        return safe.Length > 0 ? safe : "attachment";
    }

    private sealed record ProcessedFile(string StorageKey, AttachmentKind Kind, string ContentType, string Sha256, string OriginalFileName, MemoryStream Data);
}
