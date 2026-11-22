namespace PHANTOM.Domain.Incidents;

public enum AnalysisStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed,
}

/// <summary>Sanitised reasons an analysis failed. Provider error bodies are never stored.</summary>
public enum AnalysisFailureCode
{
    Timeout,
    ProviderError,
    InvalidOutput,
    RateLimited,
    NotConfigured,
}

/// <summary>
/// One AI analysis attempt for an incident. Moves Pending → Processing (claimed by the worker)
/// → Succeeded or Failed, and is immutable once finished. A retry is a new attempt.
/// </summary>
public sealed class IncidentAnalysis
{
    public const string CurrentSchemaVersion = "phantom.intel.v1";
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
    public AnalysisStatus Status { get; private set; }
    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public string SchemaVersion { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>When the worker claimed the attempt; used to recover attempts stuck after a crash.</summary>
    public DateTimeOffset? ClaimedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }
    public int? LatencyMs { get; private set; }

    /// <summary>Validated JSON result matching <see cref="SchemaVersion"/>. Null unless Succeeded.</summary>
    public string? Result { get; private set; }

    public Severity? RecommendedSeverity { get; private set; }
    public IncidentCategory? RecommendedCategory { get; private set; }
    public AnalysisFailureCode? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public bool UsedImageInput { get; private set; }
    public bool UsedAudioTranscript { get; private set; }

    /// <summary>Null when triggered by the system (normal intake). Set when a user requested a retry.</summary>
    public Guid? RequestedById { get; private set; }

    public bool IsFinished => Status is AnalysisStatus.Succeeded or AnalysisStatus.Failed;

    public static IncidentAnalysis CreatePending(
        Guid id,
        Guid incidentId,
        Guid reportId,
        int attempt,
        DateTimeOffset requestedAt,
        Guid? requestedById = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return new IncidentAnalysis
        {
            Id = id,
            IncidentId = incidentId,
            ReportId = reportId,
            Attempt = attempt,
            Status = AnalysisStatus.Pending,
            SchemaVersion = CurrentSchemaVersion,
            RequestedAt = requestedAt,
            RequestedById = requestedById,
        };
    }

    public void Succeed(string provider, string model, string resultJson, Severity severity, IncidentCategory category, int latencyMs, DateTimeOffset completedAt,
        bool usedImageInput = false, bool usedAudioTranscript = false)
    {
        EnsureProcessing();
        UsedImageInput = usedImageInput;
        UsedAudioTranscript = usedAudioTranscript;
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        Status = AnalysisStatus.Succeeded;
        Provider = Bound(provider, MaxProviderLength);
        Model = Bound(model, MaxModelLength);
        Result = resultJson;
        RecommendedSeverity = severity;
        RecommendedCategory = category;
        LatencyMs = Math.Max(0, latencyMs);
        CompletedAt = completedAt;
    }

    public void Fail(AnalysisFailureCode code, string message, string? provider, string? model, int latencyMs, DateTimeOffset completedAt)
    {
        EnsureProcessing();
        Status = AnalysisStatus.Failed;
        FailureCode = code;
        FailureMessage = Bound(message, MaxFailureMessageLength);
        Provider = provider is null ? null : Bound(provider, MaxProviderLength);
        Model = model is null ? null : Bound(model, MaxModelLength);
        LatencyMs = Math.Max(0, latencyMs);
        CompletedAt = completedAt;
    }

    private void EnsureProcessing()
    {
        if (Status != AnalysisStatus.Processing)
        {
            throw new InvalidOperationException($"Analysis {Id} is {Status}; only a claimed (Processing) analysis can finish.");
        }
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
