using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Application settings (Requirement 17): admin-only, validated, audited, effective at run time, no secrets.</summary>
[Collection(ApiCollection.Name)]
public sealed class SettingsTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Fields =
        ["categoryWeight", "geoRadiusMeters", "geoWeight", "locationWeight", "suggestThreshold", "textWeight", "timeWeight", "weatherCacheMinutes", "windowHours"];

    [Fact]
    public async Task Administrators_change_settings_which_are_audited_and_guarded_against_stale_saves()
    {
        var (adminId, admin) = await AdminAsync();
        var current = await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings", Ct);
        try
        {
            var values = current!["values"]!.DeepClone().AsObject();
            values["suggestThreshold"] = 0.7;
            values["weatherCacheMinutes"] = 30;
            var saved = await admin.PutAsJsonAsync("/api/v1/settings", new JsonObject { ["values"] = values, ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var after = (await saved.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            Assert.True(after["isCustomised"]!.GetValue<bool>());
            Assert.Equal(0.7, after["values"]!["suggestThreshold"]!.GetValue<double>());
            Assert.Equal(30, after["values"]!["weatherCacheMinutes"]!.GetValue<int>());
            Assert.Equal(0.55, after["defaults"]!["suggestThreshold"]!.GetValue<double>());
            Assert.Equal(adminId, after["updatedBy"]!["id"]!.GetValue<Guid>());

            // Requirement 17.2: audited with the values before and after.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var entry = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs
                    .Where(a => a.Action == AuditActions.SettingsChanged && a.ActorUserId == adminId).OrderByDescending(a => a.Id).FirstAsync(Ct);
                Assert.Equal(current["values"]!["suggestThreshold"]!.GetValue<double>(), JsonNode.Parse(entry.PreviousValue!)!["suggestThreshold"]!.GetValue<double>());
                Assert.Equal(0.7, JsonNode.Parse(entry.NewValue!)!["suggestThreshold"]!.GetValue<double>());
            }

            // Someone saving over the version they read before this change is refused.
            var stale = await admin.PutAsJsonAsync("/api/v1/settings", new JsonObject { ["values"] = values.DeepClone(), ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }
        finally
        {
            await RestoreDefaultsAsync(admin);
        }
    }

    [Theory]
    [InlineData("geoWeight", 1.5)]
    [InlineData("windowHours", 0)]
    [InlineData("geoRadiusMeters", 10)]
    [InlineData("suggestThreshold", 0)]
    [InlineData("weatherCacheMinutes", 5000)]
    public async Task Out_of_range_values_are_rejected(string field, double value)
    {
        var (_, admin) = await AdminAsync();
        var current = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings", Ct))!;
        var values = current["values"]!.DeepClone().AsObject();
        values[field] = value;
        var response = await admin.PutAsJsonAsync("/api/v1/settings", new JsonObject { ["values"] = values, ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task All_weights_zero_is_rejected()
    {
        var (_, admin) = await AdminAsync();
        var current = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings", Ct))!;
        var values = current["values"]!.DeepClone().AsObject();
        foreach (var w in new[] { "geoWeight", "timeWeight", "categoryWeight", "textWeight", "locationWeight" })
        {
            values[w] = 0;
        }

        var response = await admin.PutAsJsonAsync("/api/v1/settings", new JsonObject { ["values"] = values, ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_changed_radius_takes_effect_in_duplicate_detection_immediately()
    {
        var (_, admin) = await AdminAsync();
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var lat = 11 + Random.Shared.NextDouble();
        var lon = 9 + Random.Shared.NextDouble();
        var tag = $"zq{Guid.NewGuid():N}"[..10];
        try
        {
            // ~450 m apart: a duplicate under the 1.5 km default, not under a 100 m radius.
            var before1 = await SubmitAsync(tag, lat, lon);
            var before2 = await SubmitAsync(tag, lat + 0.004, lon);
            Assert.NotEmpty(await RelationsAsync(coordinator, before2));
            _ = before1;

            var current = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings", Ct))!;
            var values = current["values"]!.DeepClone().AsObject();
            values["geoRadiusMeters"] = 100;
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings", new JsonObject { ["values"] = values, ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct)).StatusCode);

            var lat2 = lat + 0.5;
            await SubmitAsync(tag + "b", lat2, lon);
            var after2 = await SubmitAsync(tag + "b", lat2 + 0.004, lon);
            Assert.Empty(await RelationsAsync(coordinator, after2));
        }
        finally
        {
            await RestoreDefaultsAsync(admin);
        }
    }

    [Fact]
    public async Task Only_administrators_see_settings_and_the_response_holds_only_the_listed_values()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/v1/settings", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PutAsJsonAsync("/api/v1/settings", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/settings", Ct)).StatusCode);

        var (_, admin) = await AdminAsync();
        var body = await admin.GetStringAsync("/api/v1/settings", Ct);
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(["defaults", "isCustomised", "updatedAt", "updatedBy", "values"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(Fields, json["values"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var secretish in new[] { "apikey", "secret", "password", "token", "connection", "signing" })
        {
            Assert.DoesNotContain(secretish, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<(Guid Id, HttpClient Client)> AdminAsync()
    {
        var user = await factory.CreateUserAsync(Role.AdministratorId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (user.Id, client);
    }

    private static async Task RestoreDefaultsAsync(HttpClient admin)
    {
        var current = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings", Ct))!;
        var restored = await admin.PutAsJsonAsync("/api/v1/settings",
            new JsonObject { ["values"] = current["defaults"]!.DeepClone(), ["expectedUpdatedAt"] = current["updatedAt"]?.DeepClone() }, Ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        // Saved values equal to the defaults are not "customised".
        Assert.False((await restored.Content.ReadFromJsonAsync<JsonObject>(Ct))!["isCustomised"]!.GetValue<bool>());
    }

    private static async Task<JsonArray> RelationsAsync(HttpClient client, Guid incidentId) =>
        (await client.GetFromJsonAsync<JsonArray>($"/api/v1/incidents/{incidentId}/relations", Ct))!;

    private async Task<Guid> SubmitAsync(string tag, double latitude, double longitude)
    {
        var description = $"Fire at the {tag} depot gate, flames and black smoke {Guid.NewGuid():N}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent($"Fire at {tag} depot"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"{tag} depot gate"), "locationText" },
            { new StringContent(latitude.ToString(CultureInfo.InvariantCulture)), "latitude" },
            { new StringContent(longitude.ToString(CultureInfo.InvariantCulture)), "longitude" },
        };
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }
}
