namespace PHANTOM.Domain.Weather;

/// <summary>Why a weather retrieval failed (Requirement 9.5). Values are persisted; never rename.</summary>
public enum WeatherFailureReason
{
    Timeout,
    Unreachable,
    ProviderError,
    InvalidResponse,
    NotConfigured,
}

/// <summary>
/// One weather retrieval for an incident (Requirement 9.2): who supplied it, when, and for which (rounded)
/// coordinates. A failed retrieval is kept too, so the reason can be shown and retries spaced out.
/// Context only; never evidence of an emergency or a prediction (9.6). Append-only.
/// </summary>
public sealed class EnvironmentalSnapshot
{
    public const int MaxProviderLength = 100;
    public const int MaxMessageLength = 500;

    private EnvironmentalSnapshot()
    {
        // EF Core
        Provider = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public string Provider { get; private set; }

    /// <summary>When PHANTOM obtained the reading from the provider (a cached reading keeps its original time).</summary>
    public DateTimeOffset RetrievedAt { get; private set; }

    /// <summary>The coordinates sent to the provider: the incident's, rounded to 2 dp (about 1 km).</summary>
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }

    public bool Succeeded { get; private set; }

    /// <summary>The reading as JSON (temperature, precipitation, wind, visibility, observation time).</summary>
    public string? Data { get; private set; }

    public WeatherFailureReason? FailureReason { get; private set; }
    public string? FailureMessage { get; private set; }

    /// <summary>A recorded reading replayed in test environments, labelled as such everywhere.</summary>
    public bool IsFixture { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static EnvironmentalSnapshot Success(Guid id, Guid incidentId, string provider, DateTimeOffset retrievedAt,
        decimal latitude, decimal longitude, string data, bool isFixture, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        return new EnvironmentalSnapshot
        {
            Id = id,
            IncidentId = incidentId,
            Provider = Bound(provider, MaxProviderLength),
            RetrievedAt = retrievedAt,
            Latitude = latitude,
            Longitude = longitude,
            Succeeded = true,
            Data = data,
            IsFixture = isFixture,
            CreatedAt = now,
        };
    }

    public static EnvironmentalSnapshot Failure(Guid id, Guid incidentId, string provider, decimal latitude, decimal longitude,
        WeatherFailureReason reason, string message, DateTimeOffset now) =>
        new()
        {
            Id = id,
            IncidentId = incidentId,
            Provider = Bound(provider, MaxProviderLength),
            RetrievedAt = now,
            Latitude = latitude,
            Longitude = longitude,
            Succeeded = false,
            FailureReason = reason,
            FailureMessage = Bound(message, MaxMessageLength),
            CreatedAt = now,
        };

    private static string Bound(string value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
