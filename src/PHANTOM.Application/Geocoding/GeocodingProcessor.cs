using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Geocoding;

public interface IGeocodingProcessor
{
    /// <summary>Geocodes one claimed (Processing) incident. Called by the background worker.</summary>
    Task ProcessAsync(Guid incidentId, CancellationToken cancellationToken);
}

/// <summary>
/// Tries the reporter's location text first, then AI-extracted place names (Requirement 8.2-8.4).
/// A match from an AI-extracted name is stored with provenance AiInferred; nothing is ever verified here.
/// </summary>
internal sealed class GeocodingProcessor(
    IPhantomDbContext db,
    IGeocodingService geocoder,
    IAuditWriter audit,
    IClock clock,
    ILogger<GeocodingProcessor> logger) : IGeocodingProcessor
{
    private const int MaxQueries = 4;
    private const int MaxConcurrencyRetries = 3;

    public async Task ProcessAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null || incident.GeocodingStatus != GeocodingStatus.Processing)
        {
            return;
        }

        var queries = new List<(string Query, bool FromAi)> { (incident.Location.Text, false) };
        var aiPlaces = await AiPlacesAsync(incidentId, cancellationToken);
        queries.AddRange(aiPlaces
            .Where(p => !string.Equals(p.Trim(), incident.Location.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, true)));

        string? failure = null;
        foreach (var (query, fromAi) in queries.Take(MaxQueries))
        {
            var outcome = await geocoder.GeocodeAsync(query, cancellationToken);
            if (outcome.Kind == GeocodeOutcomeKind.Found)
            {
                var saved = await SaveAsync(incidentId, cancellationToken: cancellationToken, apply: (current, now) =>
                {
                    var geocoded = current.Location.WithGeocodedCoordinates(outcome.Latitude!.Value, outcome.Longitude!.Value, outcome.Precision, fromAi, outcome.PlaceName);
                    if (geocoded.IsFailure || !current.ApplyGeocode(geocoded.Value, now))
                    {
                        return false;
                    }

                    audit.RecordSystem(AuditActions.LocationGeocoded, nameof(Incident), current.Id.ToString(),
                        new { provenance = LocationProvenance.Reported.ToString(), latitude = (decimal?)null, longitude = (decimal?)null },
                        new
                        {
                            provenance = current.Location.Provenance.ToString(),
                            precision = current.Location.Precision.ToString(),
                            current.Location.Latitude,
                            current.Location.Longitude,
                            placeName = current.Location.GeocoderPlaceName,
                        },
                        new { source = fromAi ? "AI-extracted place name" : "reported location text" });
                    return true;
                });
                if (saved)
                {
                    logger.LogInformation("Incident {IncidentId} geocoded ({Precision}, from {Source})", incidentId, outcome.Precision, fromAi ? "AI place" : "location text");
                }
                else
                {
                    logger.LogInformation("Geocoding of incident {IncidentId} was superseded before saving", incidentId);
                }

                return;
            }

            if (outcome.Kind == GeocodeOutcomeKind.Failed)
            {
                failure = outcome.Detail;
                break; // the provider is unavailable; more queries would fail the same way
            }
        }

        await SaveAsync(incidentId, cancellationToken: cancellationToken, apply: (current, now) =>
        {
            current.MarkGeocodingUnsuccessful(noResult: failure is null, now);
            audit.RecordSystem(AuditActions.LocationGeocodingFailed, nameof(Incident), current.Id.ToString(),
                newValue: new { status = current.GeocodingStatus.ToString(), reason = failure ?? "No usable match for the location text or AI-extracted places.", queries = Math.Min(queries.Count, MaxQueries) });
            return true;
        });
        logger.LogInformation("Incident {IncidentId} could not be geocoded ({Outcome}); flagged for verification", incidentId, failure is null ? "no result" : "provider failure");
    }

    /// <summary>System write that yields to concurrent human changes: reload and re-apply on conflict.</summary>
    private async Task<bool> SaveAsync(Guid incidentId, Func<Incident, DateTimeOffset, bool> apply, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var current = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
            if (current is null || current.GeocodingStatus != GeocodingStatus.Processing)
            {
                // Superseded (re-queued or verified): this attempt stands down.
                return false;
            }

            if (!apply(current, clock.UtcNow))
            {
                // Already located or verified by other means: release the claim rather than leave it in Processing.
                if (current.ReleaseGeocodingClaim())
                {
                    await db.SaveChangesAsync(cancellationToken);
                }

                return false;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxConcurrencyRetries)
            {
                // Detach rather than reload: a reload does not revert a replaced owned Location, so the
                // next attempt would see stale in-memory coordinates. Detached, it reads a fresh incident.
                foreach (var entry in ex.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> AiPlacesAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var result = await db.IncidentAnalyses.AsNoTracking()
            .Where(a => a.IncidentId == incidentId && a.Status == AnalysisStatus.Succeeded && a.Result != null)
            .OrderByDescending(a => a.CompletedAt)
            .Select(a => a.Result)
            .FirstOrDefaultAsync(cancellationToken);
        var parsed = IntelligenceResultParser.Parse(result);
        return parsed.IsSuccess ? parsed.Value.NamedLocations : [];
    }
}
