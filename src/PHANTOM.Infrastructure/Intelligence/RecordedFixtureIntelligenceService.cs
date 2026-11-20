using System.Text.Json;
using PHANTOM.Application.Intelligence;

namespace PHANTOM.Infrastructure.Intelligence;

/// <summary>
/// Test-environment stand-in for the AI provider (design §12): replays one real gemini-3.8-flash response,
/// recorded on 2026-11-20, for every report. Provider "RecordedFixture" is labelled as not-a-live-analysis in
/// the API, the UI and the audit trail. Registration refuses any environment other than Development/Testing.
/// </summary>
internal sealed class RecordedFixtureIntelligenceService : IIncidentIntelligenceService
{
    public const string ResourceName = "PHANTOM.Fixtures.transformer-fire.gemini-3.8-flash.json";

    private static readonly Lazy<(string Model, string RawJson)> Recording = new(Load);

    public static string Model => Recording.Value.Model;

    public Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (model, rawJson) = Recording.Value;
        return Task.FromResult(IntelligenceOutcome.Success(IntelligenceProviders.RecordedFixture, model, rawJson));
    }

    private static (string Model, string RawJson) Load()
    {
        using var stream = typeof(RecordedFixtureIntelligenceService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded AI fixture '{ResourceName}' is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var recordedOn = root.GetProperty("recordedAt").GetDateTimeOffset().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return ($"{root.GetProperty("model").GetString()} (recorded {recordedOn})", root.GetProperty("result").GetRawText());
    }
}
