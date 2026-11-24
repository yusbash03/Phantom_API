using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Weather;
using PHANTOM.Domain.Weather;
using PHANTOM.Infrastructure;
using PHANTOM.Infrastructure.Weather;

namespace PHANTOM.UnitTests.Weather;

/// <summary>Open-Meteo client behaviour (Requirement 9.1, 9.4, 9.5) without the network.</summary>
public sealed class WeatherServiceTests
{
    private const string Body = """
        {"current":{"time":"2026-11-20T20:45","interval":900,"temperature_2m":28.6,"precipitation":0.0,"rain":0.0,
         "wind_speed_10m":11.3,"wind_direction_10m":216,"wind_gusts_10m":22.3,"visibility":21100.0}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Parses_every_requested_field_and_rejects_anything_else()
    {
        var reading = OpenMeteoParser.Parse(JsonNode.Parse(Body))!;
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 20, 45, 0, TimeSpan.Zero), reading.ObservedAt);
        Assert.Equal(28.6m, reading.TemperatureC);
        Assert.Equal(0m, reading.PrecipitationMm);
        Assert.Equal(11.3m, reading.WindSpeedKmh);
        Assert.Equal(216, reading.WindDirectionDegrees);
        Assert.Equal(22.3m, reading.WindGustsKmh);
        Assert.Equal(21100m, reading.VisibilityMetres);

        Assert.Null(OpenMeteoParser.Parse(JsonNode.Parse("""{"error":true,"reason":"bad"}""")));
        Assert.Null(OpenMeteoParser.Parse(JsonNode.Parse("""{"current":{"temperature_2m":1}}""")));
        Assert.Null(OpenMeteoParser.Parse(null));
    }

    [Fact]
    public void Missing_values_are_null_not_zero()
    {
        var reading = OpenMeteoParser.Parse(JsonNode.Parse("""{"current":{"time":"2026-11-20T20:45","temperature_2m":30}}"""))!;
        Assert.Equal(30m, reading.TemperatureC);
        Assert.Null(reading.WindSpeedKmh);
        Assert.Null(reading.VisibilityMetres);
    }

    [Fact]
    public async Task Sends_only_rounded_coordinates_and_reuses_a_reading_for_the_cache_period()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        var service = Create(handler, cacheMinutes: 15);

        var first = await service.GetCurrentAsync(6.46m, 3.40m, Ct);
        var second = await service.GetCurrentAsync(6.46m, 3.40m, Ct);
        await service.GetCurrentAsync(6.47m, 3.40m, Ct);

        Assert.True(first.IsSuccess);
        Assert.Equal(OpenMeteoWeatherService.ProviderName, first.Provider);
        Assert.Same(first, second);
        Assert.Equal(2, handler.Requests.Count);
        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains("latitude=6.46&longitude=3.40", query, StringComparison.Ordinal);
        Assert.Contains("current=temperature_2m,precipitation,rain,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failures_are_classified_and_never_cached()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("{}") });
        var service = Create(handler, cacheMinutes: 15);

        var failed = await service.GetCurrentAsync(6.46m, 3.40m, Ct);
        Assert.Equal(WeatherFailureReason.ProviderError, failed.FailureReason);

        status = HttpStatusCode.OK;
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"nope":1}""") };
        Assert.Equal(WeatherFailureReason.InvalidResponse, (await service.GetCurrentAsync(6.46m, 3.40m, Ct)).FailureReason);

        handler.Respond = _ => throw new HttpRequestException("down", null, null);
        Assert.Equal(WeatherFailureReason.Unreachable, (await service.GetCurrentAsync(6.46m, 3.40m, Ct)).FailureReason);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_timeout()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) }) { Delay = TimeSpan.FromSeconds(5) };
        var outcome = await Create(handler, cacheMinutes: 15, timeoutSeconds: 1).GetCurrentAsync(6.46m, 3.40m, Ct);
        Assert.Equal(WeatherFailureReason.Timeout, outcome.FailureReason);
        Assert.Contains("1 seconds", outcome.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fixture_replays_a_real_recorded_reading_marked_as_a_fixture()
    {
        var outcome = await new FixtureWeatherService().GetCurrentAsync(9.07m, 7.49m, Ct);
        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsFixture);
        Assert.Equal("Open-Meteo (recorded fixture)", outcome.Provider);
        Assert.Equal(28.6m, outcome.Reading!.TemperatureC);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 20, 46, 0, TimeSpan.Zero), outcome.RetrievedAt);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_fixture_refuses_to_start_outside_test_environments(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Weather:Provider"] = WeatherOptions.FixtureProvider,
            ["Weather:WorkerEnabled"] = "false",
            // Production also requires object storage; satisfy that guard so only the fixture guard is tested.
            ["Storage:Provider"] = "S3",
            ["Storage:S3:Bucket"] = "test",
            ["Storage:S3:AccessKeyId"] = "test",
            ["Storage:S3:SecretAccessKey"] = "test",
            ["Ai:WorkerEnabled"] = "false",
            ["Mapbox:WorkerEnabled"] = "false",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddInfrastructure(configuration, new HostEnvironment(environment)));
        Assert.Contains("test environments only", error.Message, StringComparison.Ordinal);
    }

    private static OpenMeteoWeatherService Create(StubHandler handler, int cacheMinutes, int timeoutSeconds = 5) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.open-meteo.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new WeatherOptions { CacheMinutes = cacheMinutes, TimeoutSeconds = timeoutSeconds }),
            TimeProvider.System,
            NullLogger<OpenMeteoWeatherService>.Instance);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        public TimeSpan Delay { get; init; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Respond(request);
        }
    }

    private sealed class HostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PHANTOM.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
