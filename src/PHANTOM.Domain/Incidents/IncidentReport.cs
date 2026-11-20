namespace PHANTOM.Domain.Incidents;

/// <summary>An immutable public submission. Only <c>IncidentId</c> is mutable (duplicate merge).</summary>
public sealed class IncidentReport
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 5000;
    public const int MaxLocationTextLength = 500;
    public const int MaxNameLength = 200;
    public const int MaxPhoneLength = 50;
    public const int MaxEmailLength = 320;
    public const int MaxReferenceLength = 20;

    private IncidentReport()
    {
        // EF Core
        Reference = string.Empty;
        Title = string.Empty;
        Description = string.Empty;
        LocationText = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Human-readable reference matching the incident number (e.g. PH-2026-000001).</summary>
    public string Reference { get; private set; }

    /// <summary>The incident this report currently belongs to. Changes only via <see cref="ReassignTo"/>.</summary>
    public Guid IncidentId { get; private set; }

    /// <summary>The incident this report was originally submitted against; never changes.</summary>
    public Guid OriginalIncidentId { get; private set; }

    public string Title { get; private set; }
    public string Description { get; private set; }
    public IncidentCategory Category { get; private set; }
    public string LocationText { get; private set; }
    public decimal? ReportedLatitude { get; private set; }
    public decimal? ReportedLongitude { get; private set; }

    // Contact details — access restricted to staff; never sent to AI.
    public string? ReporterName { get; private set; }
    public string? ReporterPhone { get; private set; }
    public string? ReporterEmail { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>UUID from the client; prevents duplicate submissions within 24 h.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Salted SHA-256 of the client IP; for abuse analysis only, never exposed.</summary>
    public string? ClientIpHash { get; private set; }

    public bool IsDemoData { get; private set; }

    public static IncidentReport Create(
        Guid id,
        string reference,
        Guid incidentId,
        string title,
        string description,
        IncidentCategory category,
        string locationText,
        decimal? reportedLatitude,
        decimal? reportedLongitude,
        string? reporterName,
        string? reporterPhone,
        string? reporterEmail,
        DateTimeOffset submittedAt,
        string? idempotencyKey,
        string? clientIpHash,
        bool isDemoData = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(locationText);

        return new IncidentReport
        {
            Id = id,
            Reference = reference,
            IncidentId = incidentId,
            OriginalIncidentId = incidentId,
            Title = title.Trim(),
            Description = description.Trim(),
            Category = category,
            LocationText = locationText.Trim(),
            ReportedLatitude = reportedLatitude,
            ReportedLongitude = reportedLongitude,
            ReporterName = reporterName?.Trim(),
            ReporterPhone = reporterPhone?.Trim(),
            ReporterEmail = reporterEmail?.Trim(),
            SubmittedAt = submittedAt,
            IdempotencyKey = idempotencyKey,
            ClientIpHash = clientIpHash,
            IsDemoData = isDemoData,
        };
    }

    /// <summary>
    /// Associates the report with the primary incident of a confirmed duplicate. The report itself, and
    /// <see cref="OriginalIncidentId"/>, never change.
    /// </summary>
    public void ReassignTo(Guid incidentId)
    {
        if (incidentId == Guid.Empty)
        {
            throw new ArgumentException("A target incident is required.", nameof(incidentId));
        }

        IncidentId = incidentId;
    }
}
