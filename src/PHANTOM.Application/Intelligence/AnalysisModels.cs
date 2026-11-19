using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Intelligence;

/// <summary>Wakes the analysis worker after new work is committed. Polling still runs if a signal is missed.</summary>
public interface IAnalysisSignal
{
    void Notify();
}

/// <summary>One AI analysis attempt as shown to staff. <c>Result</c> is a recommendation, never an approved value.</summary>
public sealed record AnalysisResponse(
    Guid Id,
    int Attempt,
    string ReportReference,
    AnalysisStatus Status,
    string? Provider,
    string? Model,
    bool IsSimulated,
    string SchemaVersion,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    int? LatencyMs,
    Severity? RecommendedSeverity,
    IncidentCategory? RecommendedCategory,
    IntelligenceResultV1? Result,
    AnalysisFailureCode? FailureCode,
    string? FailureMessage,
    UserRef? RequestedBy);
