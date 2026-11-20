using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Intelligence;

/// <summary>
/// The only way PHANTOM talks to an LLM (Requirement 6.9). Implementations return the provider's raw
/// JSON; parsing and validation happen in the application layer so every provider is held to the
/// same schema.
/// </summary>
public interface IIncidentIntelligenceService
{
    Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Everything the model is allowed to see (Requirement 6.8, security steering): report text,
/// category, location text and timestamp. Never contact details, IPs or user identifiers.
/// </summary>
public sealed record IntelligenceRequest(
    string Title,
    string Description,
    IncidentCategory ReportedCategory,
    string LocationText,
    DateTimeOffset SubmittedAt);

public static class IntelligenceProviders
{
    /// <summary>Provider name used by the development-only simulated service; always labelled as such.</summary>
    public const string Simulated = "Simulated";

    /// <summary>
    /// A recorded real model response replayed in test environments (design §12). It is not an analysis of
    /// the report it is attached to, so it is labelled like simulated output.
    /// </summary>
    public const string RecordedFixture = "RecordedFixture";

    /// <summary>True when the output did not come from a live model looking at this report.</summary>
    public static bool IsStandIn(string? provider) => provider is Simulated or RecordedFixture;
}

public sealed record IntelligenceOutcome
{
    private IntelligenceOutcome(string provider, string model, string? rawJson, AnalysisFailureCode? failureCode, string? failureMessage)
    {
        Provider = provider;
        Model = model;
        RawJson = rawJson;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
    }

    public string Provider { get; }
    public string Model { get; }

    /// <summary>The model's JSON text. Untrusted until parsed and validated.</summary>
    public string? RawJson { get; }

    public AnalysisFailureCode? FailureCode { get; }

    /// <summary>A message written by PHANTOM, safe to store and show. Never a provider error body.</summary>
    public string? FailureMessage { get; }

    public bool IsSuccess => FailureCode is null;

    public static IntelligenceOutcome Success(string provider, string model, string rawJson) => new(provider, model, rawJson, null, null);

    public static IntelligenceOutcome Failure(string provider, string model, AnalysisFailureCode code, string message) =>
        new(provider, model, null, code, message);
}
