using System.Collections.Concurrent;
using System.Text.Json;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.IntegrationTests.Infrastructure;

/// <summary>
/// In-process stand-in for the AI provider (testing steering). Behaviour is chosen by a marker in the
/// report description, e.g. "[ai:timeout]", so tests sharing one API instance don't interfere.
/// </summary>
public sealed class ScriptedIntelligenceService : IIncidentIntelligenceService
{
    public const string Timeout = "[ai:timeout]";
    public const string InvalidOutput = "[ai:invalid]";
    public const string Throws = "[ai:throw]";
    public const string FailOnce = "[ai:fail-once]";
    public const string Hold = "[ai:hold]";
    public const string Critical = "[ai:critical]";
    public const string Place = "[ai:place]";

    /// <summary>Behaves like a text-only provider: photos in the request are not used.</summary>
    public const string IgnoreImages = "[ai:no-images]";

    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();

    public ConcurrentQueue<IntelligenceRequest> Requests { get; } = new();

    /// <summary>Lets a held analysis (marker <see cref="Hold"/>) finish. Keyed by a unique token in the description.</summary>
    public void Release(string token) => _holds.GetOrAdd(token, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task<IntelligenceOutcome> AnalyzeAsync(IntelligenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(request);
        var text = request.Description;
        var calls = _calls.AddOrUpdate(text, 1, (_, n) => n + 1);

        if (text.Contains(Hold, StringComparison.Ordinal))
        {
            var token = text[(text.IndexOf(Hold, StringComparison.Ordinal) + Hold.Length)..].Trim().Split(' ')[0];
            await _holds.GetOrAdd(token, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
        }

        if (text.Contains(Throws, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated adapter bug");
        }

        if (text.Contains(Timeout, StringComparison.Ordinal) || (text.Contains(FailOnce, StringComparison.Ordinal) && calls == 1))
        {
            return IntelligenceOutcome.Failure("Scripted", "scripted-1", AnalysisFailureCode.Timeout, "The AI provider did not respond within 30 seconds.");
        }

        if (text.Contains(InvalidOutput, StringComparison.Ordinal))
        {
            return IntelligenceOutcome.Success("Scripted", "scripted-1", "{\"category\":\"Fire\",\"severity\":\"Apocalyptic\"}");
        }

        var severity = text.Contains(Critical, StringComparison.Ordinal) ? Severity.Critical : Severity.High;
        var result = new IntelligenceResultV1(
            IncidentCategory.Infrastructure, "Scripted summary.", severity, "Scripted rationale.",
            text.Contains(Place, StringComparison.Ordinal) ? [request.LocationText, ScriptedGeocodingService.KnownLandmark] : [request.LocationText],
            Indication.Unknown, Indication.NotIndicated, ["smoke"], ["Exact address"], ["Call the reporter."], [], "Scripted.");
        return IntelligenceOutcome.Success("Scripted", "scripted-1", JsonSerializer.Serialize(result, IntelligenceResultParser.JsonOptions),
            text.Contains(IgnoreImages, StringComparison.Ordinal) ? 0 : request.Images.Count);
    }
}
