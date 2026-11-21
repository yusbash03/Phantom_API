using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Weather;
using PHANTOM.Domain.Weather;

namespace PHANTOM.Infrastructure.Weather;

public sealed class WeatherOptions
{
    public const string SectionName = "Weather";
    public const string OpenMeteoProvider = "OpenMeteo";

    /// <summary>Replays a recorded Open-Meteo reading; Development and Testing environments only.</summary>
    public const string FixtureProvider = "Fixture";

    public string Provider { get; set; } = OpenMeteoProvider;
    public Uri BaseUrl { get; set; } = new("https://api.open-meteo.com/");

    /// <summary>Requirement 9.4: per-request timeout, default 5 s.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Requirement 9.4: readings are reused per rounded coordinate for this long, default 15 min.</summary>
    public int CacheMinutes { get; set; } = 15;

    public bool WorkerEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 15;
    public int RetryFailedAfterMinutes { get; set; } = 5;
}

/// <summary>Turns an Open-Meteo <c>current</c> block into a reading; null when it is not one.</summary>
internal static class OpenMeteoParser
{
    public const string Fields = "temperature_2m,precipitation,rain,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility";

    public static WeatherReading? Parse(JsonNode? body)
    {
        if (body?["current"] is not JsonObject current
            || current["time"]?.GetValue<string>() is not { } time
            || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var observed))
        {
            return null;
        }

        decimal? Number(string name) => current[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (decimal)Math.Round(d, 2) : null;
        var direction = Number("wind_direction_10m");
        return new WeatherReading(
            new DateTimeOffset(observed, TimeSpan.Zero),
            Number("temperature_2m"),
            Number("precipitation"),
            Number("rain"),
            Number("wind_speed_10m"),
            direction is null ? null : (int)Math.Round(direction.Value) % 360,
            Number("wind_gusts_10m"),
            Number("visibility"));
    }
}

/// <summary>
/// Open-Meteo forecast API, current conditions (design §7). No key. Successful readings are cached per rounded
/// coordinate (9.4); failures are not, so the next attempt asks again.
/// </summary>
internal sealed partial class OpenMeteoWeatherService(HttpClient http, IMemoryCache cache, IOptions<WeatherOptions> options, TimeProvider time, ILogger<OpenMeteoWeatherService> logger) : IWeatherService
{
    public const string ProviderName = "Open-Meteo";

    public async Task<WeatherOutcome> GetCurrentAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var key = $"weather:{latitude.ToString("0.00", CultureInfo.InvariantCulture)},{longitude.ToString("0.00", CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(key, out WeatherOutcome? cached) && cached is not null)
        {
            return cached;
        }

        var url = string.Create(CultureInfo.InvariantCulture,
            $"v1/forecast?latitude={latitude:0.00}&longitude={longitude:0.00}&current={OpenMeteoParser.Fields}&timezone=GMT&wind_speed_unit=kmh");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogProviderStatus(logger, (int)response.StatusCode);
                return WeatherOutcome.Failure(ProviderName, WeatherFailureReason.ProviderError, $"The weather provider returned an error (HTTP {(int)response.StatusCode}).");
            }

            var reading = OpenMeteoParser.Parse(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token));
            if (reading is null)
            {
                return WeatherOutcome.Failure(ProviderName, WeatherFailureReason.InvalidResponse, "The weather provider's response could not be read.");
            }

            var outcome = WeatherOutcome.Success(ProviderName, reading, time.GetUtcNow());
            cache.Set(key, outcome, TimeSpan.FromMinutes(settings.CacheMinutes));
            return outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WeatherOutcome.Failure(ProviderName, WeatherFailureReason.Timeout, $"The weather provider did not respond within {settings.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.HttpRequestError);
            return WeatherOutcome.Failure(ProviderName, WeatherFailureReason.Unreachable, "The weather provider could not be reached.");
        }
        catch (System.Text.Json.JsonException)
        {
            return WeatherOutcome.Failure(ProviderName, WeatherFailureReason.InvalidResponse, "The weather provider's response could not be read.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Open-Meteo returned {StatusCode}")]
    private static partial void LogProviderStatus(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Open-Meteo request failed ({Error})")]
    private static partial void LogUnreachable(ILogger logger, HttpRequestError error);
}

/// <summary>
/// Test-environment stand-in: replays one real Open-Meteo reading recorded for Lagos on 2026-11-20, for any
/// coordinates, marked <c>IsFixture</c> so the UI labels it. Never registered outside Development/Testing.
/// </summary>
internal sealed class FixtureWeatherService : IWeatherService
{
    public const string ResourceName = "PHANTOM.Fixtures.open-meteo-lagos.json";
    public const string ProviderName = "Open-Meteo (recorded fixture)";

    private static readonly Lazy<(WeatherReading Reading, DateTimeOffset RecordedAt)> Recording = new(() =>
    {
        using var stream = typeof(FixtureWeatherService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded weather fixture '{ResourceName}' is missing.");
        var root = JsonNode.Parse(stream) ?? throw new InvalidOperationException("Weather fixture is empty.");
        var reading = OpenMeteoParser.Parse(root["response"]) ?? throw new InvalidOperationException("Weather fixture is not a valid reading.");
        return (reading, DateTimeOffset.Parse(root["recordedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture));
    });

    public Task<WeatherOutcome> GetCurrentAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken) =>
        Task.FromResult(WeatherOutcome.Success(ProviderName, Recording.Value.Reading, Recording.Value.RecordedAt, isFixture: true));
}
