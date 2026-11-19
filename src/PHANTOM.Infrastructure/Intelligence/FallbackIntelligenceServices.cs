using System.Text.Json;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Infrastructure.Intelligence;

/// <summary>
/// Development-only stand-in when no AI key is configured (architecture steering). Deterministic keyword
/// rules, provider "Simulated", and the summary says so — its output is never mistaken for a model's.
/// Never registered outside Development.
/// </summary>
internal sealed class SimulatedIntelligenceService : IIncidentIntelligenceService
{
    public const string ModelName = "simulated-keywords-v1";

    public Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = $"{request.Title} {request.Description}".ToUpperInvariant();
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));

        var trapped = Has("TRAPPED", "STUCK INSIDE", "CAN'T GET OUT");
        var injured = Has("INJURED", "BLEEDING", "UNCONSCIOUS", "CASUALT", "DEAD");
        var severity = trapped || Has("EXPLOSION", "COLLAPSE") ? Severity.Critical
            : injured || Has("FIRE", "FLOOD", "SMOKE") ? Severity.High
            : Has("ACCIDENT", "CRASH", "LEAK") ? Severity.Medium
            : Severity.Low;

        var result = new IntelligenceResultV1(
            request.ReportedCategory,
            $"[SIMULATED — no AI provider configured] Report titled \"{Truncate(request.Title, 120)}\" at {Truncate(request.LocationText, 120)}.",
            severity,
            "Simulated keyword rule, not a model assessment.",
            [Truncate(request.LocationText, 200)],
            injured ? Indication.Reported : Indication.Unknown,
            trapped ? Indication.Reported : Indication.Unknown,
            [],
            ["This is simulated output; configure Ai:ApiKey for a real assessment."],
            ["Review the original report directly."],
            [],
            "Simulated output for local development only.");

        return Task.FromResult(IntelligenceOutcome.Success(IntelligenceProviders.Simulated, ModelName,
            JsonSerializer.Serialize(result, IntelligenceResultParser.JsonOptions)));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Used outside Development when no AI key is set: analyses fail honestly and stay retryable.</summary>
internal sealed class UnconfiguredIntelligenceService : IIncidentIntelligenceService
{
    public Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(IntelligenceOutcome.Failure("none", "none", AnalysisFailureCode.NotConfigured,
            "No AI provider is configured. An administrator must set Ai__ApiKey; the analysis can then be retried."));
}
