using System.Collections.Concurrent;
using PHANTOM.Application.Weather;
using PHANTOM.Domain.Weather;

namespace PHANTOM.IntegrationTests.Infrastructure;

/// <summary>In-process weather provider for tests: never calls Open-Meteo. Latitude 1.11 simulates a timeout.</summary>
public sealed class ScriptedWeatherService : IWeatherService
{
    public const decimal TimeoutLatitude = 1.11m;
    public const string ProviderName = "Scripted";

    public ConcurrentQueue<(decimal Latitude, decimal Longitude)> Calls { get; } = new();

    public Task<WeatherOutcome> GetCurrentAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        Calls.Enqueue((latitude, longitude));
        if (latitude == TimeoutLatitude)
        {
            return Task.FromResult(WeatherOutcome.Failure(ProviderName, WeatherFailureReason.Timeout, "The weather provider did not respond within 5 seconds."));
        }

        var reading = new WeatherReading(new DateTimeOffset(2026, 11, 20, 20, 45, 0, TimeSpan.Zero), 28.6m, 0m, 0m, 11.3m, 216, 22.3m, 21100m);
        return Task.FromResult(WeatherOutcome.Success(ProviderName, reading, DateTimeOffset.UtcNow));
    }
}
