using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Intelligence;

namespace PHANTOM.UnitTests.Intelligence;

/// <summary>
/// Opt-in check against the real Gemini API (testing steering). Runs only when PHANTOM_LIVE_AI_KEY is set:
/// <c>PHANTOM_LIVE_AI_KEY=... dotnet test --project tests/PHANTOM.UnitTests --filter-trait "Category=Live"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class GeminiLiveTests
{
    [Fact]
    public async Task Live_model_returns_a_valid_phantom_intel_v1_result()
    {
        var key = Environment.GetEnvironmentVariable("PHANTOM_LIVE_AI_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), "Set PHANTOM_LIVE_AI_KEY to run the live Gemini check.");
        var model = Environment.GetEnvironmentVariable("PHANTOM_LIVE_AI_MODEL") ?? new AiOptions().Model;
        var options = new AiOptions { ApiKey = key, Model = model };
        using var http = new HttpClient { BaseAddress = options.BaseUrl, Timeout = Timeout.InfiniteTimeSpan };
        var service = new GeminiIntelligenceService(http, Options.Create(options), TimeProvider.System, NullLogger<GeminiIntelligenceService>.Instance);

        var outcome = await service.AnalyzeAsync(new IntelligenceRequest(
            "Building on fire, people trapped",
            "Three-storey residential building on fire, flames on the second floor. I can hear people shouting from a window upstairs. Smoke is very thick.",
            IncidentCategory.Fire,
            "Allen Avenue near the roundabout, Ikeja",
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsSuccess, $"{outcome.FailureCode}: {outcome.FailureMessage}");
        var parsed = IntelligenceResultParser.Parse(outcome.RawJson);
        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(IncidentCategory.Fire, parsed.Value.Category);
        Assert.Contains(parsed.Value.Severity, (Severity[])[Severity.High, Severity.Critical]);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"model={outcome.Model} severity={parsed.Value.Severity} trapped={parsed.Value.PossibleTrappedPersons} summary={parsed.Value.Summary}");
    }
}
