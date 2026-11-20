using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Operations map data (Requirement 8.7, 8.8).</summary>
[Collection(ApiCollection.Name)]
public sealed class IncidentMapTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A box far from every other test's coordinates, so results are deterministic.
    private const string Box = "minLatitude=-40.5&maxLatitude=-40.0&minLongitude=-70.5&maxLongitude=-70.0";

    [Fact]
    public async Task Map_returns_located_incidents_with_honest_location_labels_and_counts_the_rest()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var reported = await SubmitAsync("Flood", -40.2, -70.2);
        var verified = await SubmitAsync("Flood", -40.3, -70.3);
        await SubmitAsync("Flood", null, null);
        var outside = await SubmitAsync("Flood", -10.0, -50.0);
        var detail = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{verified}", Ct);
        var put = await coordinator.PutAsJsonAsync($"/api/v1/incidents/{verified}/location",
            new { text = "Checked on the phone with the caller", latitude = -40.31, longitude = -70.31, version = detail.GetProperty("version").GetInt32() }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var map = await MapAsync(coordinator, $"{Box}&category=Flood");

        var ids = map.Items.Select(i => i.Id).ToList();
        Assert.Contains(reported, ids);
        Assert.Contains(verified, ids);
        Assert.DoesNotContain(outside, ids);
        var plain = map.Items.Single(i => i.Id == reported);
        Assert.False(plain.IsVerified);
        Assert.Equal("Reported", plain.Provenance);
        var confirmed = map.Items.Single(i => i.Id == verified);
        Assert.True(confirmed.IsVerified);
        Assert.Equal("ManuallyVerified", confirmed.Provenance);
        Assert.Equal(-40.31m, confirmed.Latitude);
        Assert.True(map.WithoutCoordinates >= 1);
        Assert.False(map.Truncated);
        Assert.All(map.Items, i => Assert.Equal(i.Provenance == "ManuallyVerified", i.IsVerified));
    }

    [Fact]
    public async Task Map_filters_by_status_category_and_severity()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var fire = await SubmitAsync("Fire", -40.4, -70.4);
        var security = await SubmitAsync("Security", -40.41, -70.41);

        Assert.DoesNotContain(security, (await MapAsync(coordinator, $"{Box}&category=Fire")).Items.Select(i => i.Id));
        Assert.Contains(fire, (await MapAsync(coordinator, $"{Box}&status=Reported")).Items.Select(i => i.Id));
        Assert.DoesNotContain(fire, (await MapAsync(coordinator, $"{Box}&status=Verified")).Items.Select(i => i.Id));
        Assert.DoesNotContain(fire, (await MapAsync(coordinator, $"{Box}&severity=Critical")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Partial_bounding_box_is_rejected_and_responders_cannot_see_the_map()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.GetAsync("/api/v1/incidents/map?minLatitude=1&maxLatitude=2", Ct)).StatusCode);

        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync("/api/v1/incidents/map", Ct)).StatusCode);
    }

    private async Task<Guid> SubmitAsync(string category, double? lat, double? lon)
    {
        var description = $"Map test report {Guid.NewGuid():N}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Map test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent(category), "category" },
            { new StringContent($"Map test location {Guid.NewGuid():N}"), "locationText" },
        };
        if (lat is not null)
        {
            form.Add(new StringContent(lat.Value.ToString(CultureInfo.InvariantCulture)), "latitude");
            form.Add(new StringContent(lon!.Value.ToString(CultureInfo.InvariantCulture)), "longitude");
        }

        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents
            .Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }

    private static async Task<MapBody> MapAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/incidents/map?{query}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<MapBody>(Json, Ct))!;
    }

    private sealed record MapItem(Guid Id, string Number, decimal Latitude, decimal Longitude, string Provenance, bool IsVerified);

    private sealed record MapBody(List<MapItem> Items, int WithoutCoordinates, bool Truncated);
}
