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
/// category, location text, timestamp and the report's photos (6.10). Never contact details, IPs or user
/// identifiers. Photos are the stored copies, re-encoded at intake so their metadata (e.g. GPS) is gone.
/// </summary>
public sealed record IntelligenceRequest(
    string Title,
    string Description,
    IncidentCategory ReportedCategory,
    string LocationText,
    DateTimeOffset SubmittedAt)
{
    public IReadOnlyList<IntelligenceImage> Images { get; init; } = [];

    /// <summary>Automatic transcript of the report's voice recording, with contact details removed (6.8, 6.11).</summary>
    public string? AudioTranscript { get; init; }
}

/// <summary>A photo from the report, as stored (JPEG, PNG or WebP).</summary>
public sealed record IntelligenceImage(Guid AttachmentId, string ContentType, byte[] Data);

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

    /// <summary>How many of the request's photos the provider actually received (0 for stand-ins that ignore them).</summary>
    public int ImagesUsed { get; private init; }

    /// <summary>Whether the provider received the voice recording transcript (false for stand-ins that ignore input).</summary>
    public bool TranscriptUsed { get; private init; }

    public static IntelligenceOutcome Success(string provider, string model, string rawJson, int imagesUsed = 0, bool transcriptUsed = false) =>
        new(provider, model, rawJson, null, null) { ImagesUsed = Math.Max(0, imagesUsed), TranscriptUsed = transcriptUsed };

    public static IntelligenceOutcome Failure(string provider, string model, AnalysisFailureCode code, string message) =>
        new(provider, model, null, code, message);
}
