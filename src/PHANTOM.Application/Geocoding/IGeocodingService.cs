using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Geocoding;

/// <summary>Server-side forward geocoding (Requirement 8.2). Implementations never invent coordinates (8.4).</summary>
public interface IGeocodingService
{
    Task<GeocodeOutcome> GeocodeAsync(string query, CancellationToken cancellationToken);
}

public enum GeocodeOutcomeKind
{
    Found,

    /// <summary>Nothing usable: no match, or only a match too coarse to place a pin (e.g. a whole state).</summary>
    NoResult,

    /// <summary>The provider could not be used (error, timeout, not configured).</summary>
    Failed,
}

public sealed record GeocodeOutcome(
    GeocodeOutcomeKind Kind,
    decimal? Latitude,
    decimal? Longitude,
    LocationPrecision Precision,
    string? PlaceName,
    string? Detail)
{
    public static GeocodeOutcome Found(decimal latitude, decimal longitude, LocationPrecision precision, string? placeName) =>
        new(GeocodeOutcomeKind.Found, latitude, longitude, precision, placeName, null);

    public static GeocodeOutcome NoResult(string detail) => new(GeocodeOutcomeKind.NoResult, null, null, LocationPrecision.Unknown, null, detail);

    public static GeocodeOutcome Failed(string detail) => new(GeocodeOutcomeKind.Failed, null, null, LocationPrecision.Unknown, null, detail);
}

/// <summary>Wakes the geocoding worker after new work is committed. Polling covers anything missed.</summary>
public interface IGeocodingSignal
{
    void Notify();
}
