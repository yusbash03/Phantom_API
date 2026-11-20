using System.Collections.Concurrent;
using PHANTOM.Application.Geocoding;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.IntegrationTests.Infrastructure;

/// <summary>In-process geocoder for tests. Defaults to "no result" so incidents in other tests are unaffected.</summary>
public sealed class ScriptedGeocodingService : IGeocodingService
{
    public const string Found = "[geo:found]";
    public const string Fail = "[geo:fail]";
    public const string KnownLandmark = "Zarmaq Known Landmark";

    public ConcurrentQueue<string> Queries { get; } = new();

    public Task<GeocodeOutcome> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        Queries.Enqueue(query);
        if (query.Contains(Fail, StringComparison.Ordinal))
        {
            return Task.FromResult(GeocodeOutcome.Failed("The geocoding provider returned an error (HTTP 503)."));
        }

        if (query.Contains(Found, StringComparison.Ordinal))
        {
            return Task.FromResult(GeocodeOutcome.Found(6.601838m, 3.351472m, LocationPrecision.Street, "12 Allen Avenue, Ikeja, Lagos"));
        }

        return Task.FromResult(query == KnownLandmark
            ? GeocodeOutcome.Found(9.896527m, 8.858331m, LocationPrecision.Locality, "Zarmaq Landmark, Jos")
            : GeocodeOutcome.NoResult("No match found."));
    }
}
