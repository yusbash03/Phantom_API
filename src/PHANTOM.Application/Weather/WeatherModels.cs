using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Weather;

namespace PHANTOM.Application.Weather;

/// <summary>Current conditions (Requirement 9.1). Null where the provider did not report a value.</summary>
public sealed record WeatherReading(
    DateTimeOffset ObservedAt,
    decimal? TemperatureC,
    decimal? PrecipitationMm,
    decimal? RainMm,
    decimal? WindSpeedKmh,
    int? WindDirectionDegrees,
    decimal? WindGustsKmh,
    decimal? VisibilityMetres);

public sealed record WeatherOutcome
{
    private WeatherOutcome(string provider, WeatherReading? reading, DateTimeOffset? retrievedAt, bool isFixture, WeatherFailureReason? reason, string? message)
    {
        Provider = provider;
        Reading = reading;
        RetrievedAt = retrievedAt;
        IsFixture = isFixture;
        FailureReason = reason;
        FailureMessage = message;
    }

    public string Provider { get; }
    public WeatherReading? Reading { get; }
    public DateTimeOffset? RetrievedAt { get; }
    public bool IsFixture { get; }
    public WeatherFailureReason? FailureReason { get; }
    public string? FailureMessage { get; }
    public bool IsSuccess => Reading is not null;

    public static WeatherOutcome Success(string provider, WeatherReading reading, DateTimeOffset retrievedAt, bool isFixture = false) =>
        new(provider, reading, retrievedAt, isFixture, null, null);

    public static WeatherOutcome Failure(string provider, WeatherFailureReason reason, string message) =>
        new(provider, null, null, false, reason, message);
}

/// <summary>A weather provider. Implementations enforce their own timeout and cache, and never throw.</summary>
public interface IWeatherService
{
    Task<WeatherOutcome> GetCurrentAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken);
}

public enum EnvironmentStatus
{
    /// <summary>The incident has no coordinates, so there is nothing to look up.</summary>
    NoCoordinates,

    /// <summary>Coordinates are known but no retrieval for them has happened yet.</summary>
    Pending,

    Available,

    /// <summary>The latest retrieval failed (Requirement 9.5); see <c>Failure</c>.</summary>
    Unavailable,
}

public sealed record EnvironmentSnapshotResponse(
    string Provider,
    DateTimeOffset RetrievedAt,
    decimal Latitude,
    decimal Longitude,
    bool IsFixture,
    WeatherReading Reading);

public sealed record EnvironmentFailureResponse(WeatherFailureReason Reason, string Message, DateTimeOffset At);

/// <summary>
/// Weather context for an incident. <c>Snapshot</c> is the latest successful reading (which may be for earlier
/// coordinates when <c>IsForCurrentLocation</c> is false). Weather alerts are not offered by the provider.
/// </summary>
public sealed record EnvironmentResponse(
    EnvironmentStatus Status,
    IncidentCategory Category,
    bool LocationIsVerified,
    bool IsForCurrentLocation,
    EnvironmentSnapshotResponse? Snapshot,
    EnvironmentFailureResponse? Failure,
    bool AlertsSupported);
