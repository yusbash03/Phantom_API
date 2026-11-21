using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Weather context (Requirement 9): snapshots, rounding, failures that never block, staff-only access.</summary>
[Collection(ApiCollection.Name)]
public sealed class WeatherTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_incident_with_coordinates_gets_a_stored_snapshot_with_source_time_and_rounded_coordinates()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var id = await SubmitAsync($"Weather found {Guid.NewGuid():N}", 6.456789, 3.398765);

        var environment = await WaitForAsync(coordinator, id, "Available");
        var snapshot = environment.GetProperty("snapshot");
        Assert.Equal(ScriptedWeatherService.ProviderName, snapshot.GetProperty("provider").GetString());
        Assert.Equal(6.46m, snapshot.GetProperty("latitude").GetDecimal());
        Assert.Equal(3.40m, snapshot.GetProperty("longitude").GetDecimal());
        Assert.False(snapshot.GetProperty("isFixture").GetBoolean());
        Assert.True(snapshot.GetProperty("retrievedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        var reading = snapshot.GetProperty("reading");
        Assert.Equal(11.3m, reading.GetProperty("windSpeedKmh").GetDecimal());
        Assert.Equal(216, reading.GetProperty("windDirectionDegrees").GetInt32());
        Assert.Equal(22.3m, reading.GetProperty("windGustsKmh").GetDecimal());
        Assert.Equal("Fire", environment.GetProperty("category").GetString());
        Assert.False(environment.GetProperty("alertsSupported").GetBoolean());
        Assert.True(environment.GetProperty("isForCurrentLocation").GetBoolean());

        // The exact position never reaches the provider: only the rounded one.
        Assert.Contains((6.46m, 3.40m), factory.Weather.Calls);
        Assert.DoesNotContain(factory.Weather.Calls, c => c.Latitude == 6.456789m);

        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().EnvironmentalSnapshots.Where(s => s.IncidentId == id).ToListAsync(Ct);
        var only = Assert.Single(stored);
        Assert.True(only.Succeeded);
        Assert.Contains("windSpeedKmh", only.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_coordinates_there_is_nothing_to_look_up()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var id = await SubmitAsync($"Weather none {Guid.NewGuid():N}");
        var environment = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}/environment", Ct);
        Assert.Equal("NoCoordinates", environment.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, environment.GetProperty("snapshot").ValueKind);
    }

    [Fact]
    public async Task A_provider_failure_is_shown_with_its_reason_and_never_blocks_the_incident()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var id = await SubmitAsync($"Weather fails {Guid.NewGuid():N}", (double)ScriptedWeatherService.TimeoutLatitude, 3.3);

        var environment = await WaitForAsync(coordinator, id, "Unavailable");
        Assert.Equal("Timeout", environment.GetProperty("failure").GetProperty("reason").GetString());
        Assert.Contains("did not respond", environment.GetProperty("failure").GetProperty("message").GetString(), StringComparison.Ordinal);

        // The workflow carries on regardless.
        var version = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{id}/review", new { version }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Moving_the_location_retrieves_weather_for_the_new_place()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var id = await SubmitAsync($"Weather moves {Guid.NewGuid():N}", 6.5, 3.3);
        await WaitForAsync(coordinator, id, "Available");

        var version = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var moved = await coordinator.PutAsJsonAsync($"/api/v1/incidents/{id}/location", new { text = "Confirmed spot", latitude = 9.07, longitude = 7.49, version }, Ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement environment;
        do
        {
            await Task.Delay(300, Ct);
            environment = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}/environment", Ct);
        }
        while (environment.GetProperty("snapshot").GetProperty("latitude").GetDecimal() != 9.07m && DateTime.UtcNow < deadline);

        Assert.Equal(9.07m, environment.GetProperty("snapshot").GetProperty("latitude").GetDecimal());
        Assert.True(environment.GetProperty("locationIsVerified").GetBoolean());
        Assert.True(environment.GetProperty("isForCurrentLocation").GetBoolean());
    }

    [Fact]
    public async Task Staff_can_refresh_and_nobody_else_can_read_weather()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var id = await SubmitAsync($"Weather refresh {Guid.NewGuid():N}", 7.25, 5.19);

        var refreshed = await coordinator.PostAsync($"/api/v1/incidents/{id}/environment/refresh", null, Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal("Available", (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/incidents/{id}/environment", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/incidents/{id}/environment", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await coordinator.GetAsync($"/api/v1/incidents/{Guid.NewGuid()}/environment", Ct)).StatusCode);
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient client, Guid id, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var environment = await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}/environment", Ct);
            if (environment.GetProperty("status").GetString() == status)
            {
                return environment;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Weather status stayed {environment.GetProperty("status").GetString()}, expected {status}.");
            await Task.Delay(300, Ct);
        }
    }

    private async Task<Guid> SubmitAsync(string description, double? latitude = null, double? longitude = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Weather test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Weather site {Guid.NewGuid():N}"), "locationText" },
        };
        if (latitude is not null)
        {
            form.Add(new StringContent(latitude.Value.ToString(CultureInfo.InvariantCulture)), "latitude");
            form.Add(new StringContent(longitude!.Value.ToString(CultureInfo.InvariantCulture)), "longitude");
        }

        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }
}
