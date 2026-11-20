using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Server-side geocoding and manual verification (Requirement 8.1-8.6; critical property 4).</summary>
[Collection(ApiCollection.Name)]
public sealed class GeocodingTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Location_text_is_geocoded_as_approximate_and_never_verified()
    {
        var id = await SubmitAsync($"12 Allen Avenue {ScriptedGeocodingService.Found} {Guid.NewGuid():N}");

        var incident = await WaitForGeocodingAsync(id);

        Assert.Equal(GeocodingStatus.Succeeded, incident.GeocodingStatus);
        Assert.Equal(6.601838m, incident.Location.Latitude);
        Assert.Equal(3.351472m, incident.Location.Longitude);
        Assert.Equal(LocationProvenance.Geocoded, incident.Location.Provenance);
        Assert.Equal(LocationPrecision.Street, incident.Location.Precision);
        Assert.Equal("12 Allen Avenue, Ikeja, Lagos", incident.Location.GeocoderPlaceName);
        Assert.False(incident.Location.IsVerified);
        Assert.False(incident.LocationNeedsVerification);
        Assert.Equal(0, incident.Version);

        var detail = await (await CoordinatorAsync()).GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct);
        Assert.False(detail.GetProperty("location").GetProperty("isVerified").GetBoolean());
        Assert.Equal("Geocoded", detail.GetProperty("location").GetProperty("provenance").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.LocationGeocoded && a.EntityId == id.ToString(), Ct);
        Assert.Equal(AuditActorType.System, audit.ActorType);
    }

    [Fact]
    public async Task No_result_keeps_the_text_without_coordinates_and_flags_the_incident()
    {
        var id = await SubmitAsync($"Somewhere behind the old market {Guid.NewGuid():N}");

        var incident = await WaitForGeocodingAsync(id);

        Assert.Equal(GeocodingStatus.NoResult, incident.GeocodingStatus);
        Assert.False(incident.Location.HasCoordinates);
        Assert.True(incident.LocationNeedsVerification);
        Assert.StartsWith("Somewhere behind the old market", incident.Location.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_is_recorded_and_flagged_without_inventing_coordinates()
    {
        var id = await SubmitAsync($"Ring road {ScriptedGeocodingService.Fail} {Guid.NewGuid():N}");

        var incident = await WaitForGeocodingAsync(id);

        Assert.Equal(GeocodingStatus.Failed, incident.GeocodingStatus);
        Assert.False(incident.Location.HasCoordinates);
        Assert.True(incident.LocationNeedsVerification);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs
            .AnyAsync(a => a.Action == AuditActions.LocationGeocodingFailed && a.EntityId == id.ToString(), Ct));
    }

    [Fact]
    public async Task Ai_extracted_place_is_geocoded_with_ai_inferred_provenance_when_the_text_fails()
    {
        var id = await SubmitAsync($"Vague spot {Guid.NewGuid():N}", description: $"Fire near the big landmark {ScriptedIntelligenceService.Place}");

        var incident = await WaitForAsync(id, i => i.GeocodingStatus == GeocodingStatus.Succeeded, timeoutSeconds: 25);

        Assert.Equal(LocationProvenance.AiInferred, incident.Location.Provenance);
        Assert.Equal(9.896527m, incident.Location.Latitude);
        Assert.Equal("Zarmaq Landmark, Jos", incident.Location.GeocoderPlaceName);
        Assert.False(incident.Location.IsVerified);
        Assert.StartsWith("Vague spot", incident.Location.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reported_coordinates_are_never_sent_to_the_geocoder()
    {
        var marker = $"Exact spot {Guid.NewGuid():N}";
        var id = await SubmitAsync(marker, latitude: "6.45", longitude: "3.39");

        await Task.Delay(1500, Ct);
        var incident = await LoadAsync(id);

        Assert.Equal(GeocodingStatus.NotRequired, incident.GeocodingStatus);
        Assert.Equal(LocationProvenance.Reported, incident.Location.Provenance);
        Assert.DoesNotContain(factory.Geocoding.Queries, q => q.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_verification_is_the_only_way_to_a_verified_location()
    {
        var id = await SubmitAsync($"Allen Ave {ScriptedGeocodingService.Found} {Guid.NewGuid():N}");
        var geocoded = await WaitForGeocodingAsync(id);
        var coordinator = await CoordinatorAsync();

        var stale = await coordinator.PutAsJsonAsync($"/api/v1/incidents/{id}/location", new { text = "x", latitude = 6.6, longitude = 3.35, version = 99 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var invalid = await coordinator.PutAsJsonAsync($"/api/v1/incidents/{id}/location", new { text = "x", latitude = 95, longitude = 3.35, version = 0 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var response = await coordinator.PutAsJsonAsync($"/api/v1/incidents/{id}/location",
            new { text = "12 Allen Avenue, by the filling station", latitude = 6.60201, longitude = 3.35152, version = geocoded.Version }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var location = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("location");
        Assert.True(location.GetProperty("isVerified").GetBoolean());
        Assert.Equal("ManuallyVerified", location.GetProperty("provenance").GetString());
        Assert.Equal("Exact", location.GetProperty("precision").GetString());
        Assert.False(string.IsNullOrEmpty(location.GetProperty("verifiedBy").GetProperty("displayName").GetString()));

        var incident = await LoadAsync(id);
        Assert.Equal(1, incident.Version);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.LocationVerified && a.EntityId == id.ToString(), Ct);
        Assert.Contains("Geocoded", audit.PreviousValue, StringComparison.Ordinal);
        Assert.Contains("6.601838", audit.PreviousValue, StringComparison.Ordinal);
        Assert.Equal(AuditActorType.User, audit.ActorType);
    }

    [Fact]
    public async Task Only_coordinators_verify_locations()
    {
        var id = await SubmitAsync($"Ring road {Guid.NewGuid():N}");
        foreach (var role in new[] { Role.AdministratorId, Role.ResponderId })
        {
            var client = await factory.CreateClientForRoleAsync(role);
            var response = await client.PutAsJsonAsync($"/api/v1/incidents/{id}/location", new { text = "x", latitude = 6.6, longitude = 3.35, version = 0 }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task No_incident_is_ever_verified_without_a_verifier()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var incidents = await db.Incidents.AsNoTracking().ToListAsync(Ct);

        Assert.All(incidents, i =>
        {
            Assert.Equal(i.Location.Provenance == LocationProvenance.ManuallyVerified, i.Location.IsVerified);
            Assert.Equal(i.Location.IsVerified, i.Location.VerifiedById is not null);
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SubmitAsync(string locationText, string? description = null, string? latitude = null, string? longitude = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Smoke reported"), "title" },
            { new StringContent($"{description ?? "Smoke over the rooftops"} {Guid.NewGuid():N}"), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent(locationText), "locationText" },
        };
        if (latitude is not null)
        {
            form.Add(new StringContent(latitude), "latitude");
            form.Add(new StringContent(longitude!), "longitude");
        }

        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString();
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Number == number).Select(i => i.Id).SingleAsync(Ct);
    }

    private Task<Incident> WaitForGeocodingAsync(Guid id) =>
        WaitForAsync(id, i => i.GeocodingStatus is GeocodingStatus.Succeeded or GeocodingStatus.NoResult or GeocodingStatus.Failed);

    private async Task<Incident> WaitForAsync(Guid id, Func<Incident, bool> done, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var incident = await LoadAsync(id);
            if (done(incident))
            {
                return incident;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Geocoding did not reach the expected state (last {incident.GeocodingStatus}).");
            await Task.Delay(200, Ct);
        }
    }

    private async Task<Incident> LoadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
    }

    private Task<HttpClient> CoordinatorAsync() => factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
}
