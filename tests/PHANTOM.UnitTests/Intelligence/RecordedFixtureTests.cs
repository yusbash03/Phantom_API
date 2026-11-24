using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure;
using PHANTOM.Infrastructure.Intelligence;

namespace PHANTOM.UnitTests.Intelligence;

/// <summary>The recorded AI fixture used by the demonstration e2e (design §12): valid, labelled, test-only.</summary>
public sealed class RecordedFixtureTests
{
    private static readonly IntelligenceRequest AnyReport =
        new("Anything", "Any text at all.", IncidentCategory.Flood, "Somewhere", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Replays_a_valid_phantom_intel_v1_result_labelled_as_a_recorded_fixture()
    {
        var outcome = await new RecordedFixtureIntelligenceService().AnalyzeAsync(AnyReport, TestContext.Current.CancellationToken);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(IntelligenceProviders.RecordedFixture, outcome.Provider);
        Assert.Equal("gemini-3.8-flash (recorded 2026-11-20)", outcome.Model);
        var parsed = IntelligenceResultParser.Parse(outcome.RawJson);
        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(IncidentCategory.Fire, parsed.Value.Category);
        Assert.Equal(Severity.Medium, parsed.Value.Severity);
        Assert.Contains("electrical fire risk", parsed.Value.Hazards);
    }

    [Fact]
    public void Recorded_and_simulated_output_are_both_stand_ins_but_gemini_is_not()
    {
        Assert.True(IntelligenceProviders.IsStandIn(IntelligenceProviders.RecordedFixture));
        Assert.True(IntelligenceProviders.IsStandIn(IntelligenceProviders.Simulated));
        Assert.False(IntelligenceProviders.IsStandIn("Gemini"));
        Assert.False(IntelligenceProviders.IsStandIn(null));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Is_registered_in_test_environments(string environment)
    {
        using var provider = Build(environment).BuildServiceProvider();
        Assert.IsType<RecordedFixtureIntelligenceService>(provider.GetRequiredService<IIncidentIntelligenceService>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Refuses_to_start_anywhere_else(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(environment));
        Assert.Contains("test environments only", error.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection Build(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = AiOptions.RecordedFixtureProvider,
            ["Ai:WorkerEnabled"] = "false",
            ["Geocoding:WorkerEnabled"] = "false",
            // Production also requires object storage; satisfy that guard so only the fixture guard is tested.
            ["Storage:Provider"] = "S3",
            ["Storage:S3:Bucket"] = "test",
            ["Storage:S3:AccessKeyId"] = "test",
            ["Storage:S3:SecretAccessKey"] = "test",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new Environment(environment));
        return services;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PHANTOM.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
