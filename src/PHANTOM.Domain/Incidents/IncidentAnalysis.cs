namespace PHANTOM.Domain.Incidents;

public enum AnalysisStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed,
}

/// <summary>One AI analysis attempt for an incident. Immutable result once succeeded or failed.</summary>
public sealed class IncidentAnalysis
{
    public const int MaxProviderLength = 100;
    public const int MaxModelLength = 100;
    public const int MaxSchemaVersionLength = 50;
    public const int MaxFailureCodeLength = 50;
    public const int MaxFailureMessageLength = 500;

    private IncidentAnalysis()
    {
        // EF Core
        SchemaVersion = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ReportId { get; private set; }
    public int Attempt { get; private set; }
    public AnalysisStatus Status { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string SchemaVersion { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? LatencyMs { get; set; }

    /// <summary>Validated JSON result matching schema phantom.intel.v1. Null until Succeeded.</summary>
    public string? Result { get; set; }

    public Severity? RecommendedSeverity { get; set; }
    public IncidentCategory? RecommendedCategory { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public bool UsedImageInput { get; set; }
    public bool UsedAudioTranscript { get; set; }

    /// <summary>Null when triggered by the system (normal intake). Set when triggered by a coordinator retry.</summary>
    public Guid? RequestedById { get; private set; }

    public static IncidentAnalysis CreatePending(
        Guid id,
        Guid incidentId,
        Guid reportId,
        int attempt,
        DateTimeOffset requestedAt,
        Guid? requestedById = null)
    {
        return new IncidentAnalysis
        {
            Id = id,
            IncidentId = incidentId,
            ReportId = reportId,
            Attempt = attempt,
            Status = AnalysisStatus.Pending,
            SchemaVersion = "phantom.intel.v1",
            RequestedAt = requestedAt,
            RequestedById = requestedById,
        };
    }
}
