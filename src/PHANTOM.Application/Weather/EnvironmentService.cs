using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Weather;

namespace PHANTOM.Application.Weather;

public interface IEnvironmentService
{
    Task<Result<EnvironmentResponse>> GetAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>Retrieves now (the provider's cache still applies) and stores the snapshot.</summary>
    Task<Result<EnvironmentResponse>> RefreshAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>Worker pass: retrieve for active incidents whose current coordinates have no reading yet.</summary>
    Task<int> RetrieveDueAsync(int batchSize, TimeSpan retryFailedAfter, CancellationToken cancellationToken);
}

/// <summary>Weather context (Requirement 9). Never blocks an incident workflow: failures are recorded and shown.</summary>
internal sealed partial class EnvironmentService(IPhantomDbContext db, IWeatherService weather, IClock clock, ILogger<EnvironmentService> logger) : IEnvironmentService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IncidentStatus[] Ended = [IncidentStatus.Resolved, IncidentStatus.Closed, IncidentStatus.Rejected];

    /// <summary>About 1 km: enough for weather, and the exact position is not sent to the provider.</summary>
    public static decimal Round(decimal coordinate) => Math.Round(coordinate, 2, MidpointRounding.AwayFromZero);

    public async Task<Result<EnvironmentResponse>> GetAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        return incident is null ? NotFound() : await DescribeAsync(incident, cancellationToken);
    }

    public async Task<Result<EnvironmentResponse>> RefreshAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null)
        {
            return NotFound();
        }

        if (incident.Location.Latitude is { } lat && incident.Location.Longitude is { } lon)
        {
            await RetrieveAsync(incident.Id, lat, lon, cancellationToken);
        }

        return await DescribeAsync(incident, cancellationToken);
    }

    public async Task<int> RetrieveDueAsync(int batchSize, TimeSpan retryFailedAfter, CancellationToken cancellationToken)
    {
        var candidates = await db.Incidents.AsNoTracking()
            .Where(i => !Ended.Contains(i.Status) && i.Location.Latitude != null && i.Location.Longitude != null)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id, Latitude = i.Location.Latitude!.Value, Longitude = i.Location.Longitude!.Value })
            .Take(200)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(c => c.Id).ToList();
        var latest = await db.EnvironmentalSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.IncidentId))
            .GroupBy(s => s.IncidentId)
            .Select(g => g.OrderByDescending(s => s.CreatedAt).First())
            .ToDictionaryAsync(s => s.IncidentId, cancellationToken);

        var retryBefore = clock.UtcNow - retryFailedAfter;
        var due = candidates.Where(c =>
        {
            if (!latest.TryGetValue(c.Id, out var last))
            {
                return true;
            }

            var sameSpot = last.Latitude == Round(c.Latitude) && last.Longitude == Round(c.Longitude);
            return !sameSpot || (!last.Succeeded && last.CreatedAt < retryBefore);
        }).Take(batchSize).ToList();

        foreach (var c in due)
        {
            await RetrieveAsync(c.Id, c.Latitude, c.Longitude, cancellationToken);
        }

        return due.Count;
    }

    private async Task RetrieveAsync(Guid incidentId, decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        var lat = Round(latitude);
        var lon = Round(longitude);
        var outcome = await weather.GetCurrentAsync(lat, lon, cancellationToken);
        var now = clock.UtcNow;
        var snapshot = outcome.IsSuccess
            ? EnvironmentalSnapshot.Success(Guid.CreateVersion7(), incidentId, outcome.Provider, outcome.RetrievedAt ?? now, lat, lon,
                JsonSerializer.Serialize(outcome.Reading, JsonOptions), outcome.IsFixture, now)
            : EnvironmentalSnapshot.Failure(Guid.CreateVersion7(), incidentId, outcome.Provider, lat, lon,
                outcome.FailureReason ?? WeatherFailureReason.ProviderError, outcome.FailureMessage ?? "Weather could not be retrieved.", now);
        db.EnvironmentalSnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
        if (!outcome.IsSuccess)
        {
            LogRetrievalFailed(logger, incidentId, snapshot.FailureReason);
        }
    }

    private async Task<EnvironmentResponse> DescribeAsync(Incident incident, CancellationToken cancellationToken)
    {
        var category = incident.Category;
        var verified = incident.Location.IsVerified;
        if (incident.Location.Latitude is not { } lat || incident.Location.Longitude is not { } lon)
        {
            return new EnvironmentResponse(EnvironmentStatus.NoCoordinates, category, verified, false, null, null, AlertsSupported: false);
        }

        var latestAttempt = await db.EnvironmentalSnapshots.AsNoTracking().Where(s => s.IncidentId == incident.Id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var latestSuccess = latestAttempt is { Succeeded: true } ? latestAttempt
            : await db.EnvironmentalSnapshots.AsNoTracking().Where(s => s.IncidentId == incident.Id && s.Succeeded)
                .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);

        var snapshot = latestSuccess is null ? null : ToResponse(latestSuccess);
        var forCurrent = latestSuccess is not null && latestSuccess.Latitude == Round(lat) && latestSuccess.Longitude == Round(lon);
        var attemptedHere = latestAttempt is not null && latestAttempt.Latitude == Round(lat) && latestAttempt.Longitude == Round(lon);

        var (status, failure) = latestAttempt switch
        {
            null => (EnvironmentStatus.Pending, (EnvironmentFailureResponse?)null),
            { Succeeded: false } when attemptedHere => (EnvironmentStatus.Unavailable,
                new EnvironmentFailureResponse(latestAttempt.FailureReason ?? WeatherFailureReason.ProviderError, latestAttempt.FailureMessage ?? string.Empty, latestAttempt.CreatedAt)),
            _ when !attemptedHere => (EnvironmentStatus.Pending, null),
            _ => (EnvironmentStatus.Available, null),
        };
        return new EnvironmentResponse(status, category, verified, forCurrent, snapshot, failure, AlertsSupported: false);
    }

    private static EnvironmentSnapshotResponse? ToResponse(EnvironmentalSnapshot s)
    {
        var reading = s.Data is null ? null : JsonSerializer.Deserialize<WeatherReading>(s.Data, JsonOptions);
        return reading is null ? null : new EnvironmentSnapshotResponse(s.Provider, s.RetrievedAt, s.Latitude, s.Longitude, s.IsFixture, reading);
    }

    private static Error NotFound() => Error.NotFound("incident.not_found", "Incident not found.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Weather retrieval for incident {IncidentId} failed ({Reason})")]
    private static partial void LogRetrievalFailed(ILogger logger, Guid incidentId, WeatherFailureReason? reason);
}
