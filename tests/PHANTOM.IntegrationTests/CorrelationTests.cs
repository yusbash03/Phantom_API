using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Correlation;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Duplicate detection and decisions (Requirement 7; critical property 3: nothing is deleted or hidden).</summary>
[Collection(ApiCollection.Name)]
public sealed class CorrelationTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Second_report_of_the_same_event_is_suggested_as_a_possible_duplicate_without_changing_anything()
    {
        var tag = Tag();
        var first = await SubmitAsync(tag, 9.0500, 7.4900, "Fire");
        var second = await SubmitAsync(tag, 9.0512, 7.4907, "Fire");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        var relation = Assert.Single(await RelationsAsync(coordinator, second.Id), r => r.Other.Id == first.Id);

        Assert.Equal("PossibleDuplicate", relation.Type);
        Assert.InRange(relation.Score!.Value, 0.55m, 1m);
        Assert.StartsWith("Reported", relation.Explanation, StringComparison.Ordinal);
        Assert.Contains("same category", relation.Explanation, StringComparison.Ordinal);
        Assert.True(relation.Signals!.Value.GetProperty("geo").GetProperty("available").GetBoolean());
        Assert.Null(relation.CreatedBy);
        Assert.Equal(["ConfirmDuplicate", "Related", "Unrelated"], relation.AvailableDecisions);
        Assert.Contains(first.Id, relation.ConfirmablePrimaryIds);

        // The same pair is visible from the other side, and the list flags it.
        Assert.Single(await RelationsAsync(coordinator, first.Id), r => r.Other.Id == second.Id);
        var listed = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents?search={tag}", Json, Ct);
        Assert.All(listed.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("possibleDuplicateCount").GetInt32() >= 1));

        // Correlation alone never changes, hides or merges anything (7.4).
        foreach (var incident in new[] { first, second })
        {
            var detail = await DetailAsync(coordinator, incident.Id);
            Assert.Equal("Reported", detail.GetProperty("status").GetString());
            Assert.Single(detail.GetProperty("reports").EnumerateArray());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.RelationSuggested && a.EntityId == relation.Id.ToString(), Ct);
        Assert.Equal(AuditActorType.System, audit.ActorType);
    }

    [Fact]
    public async Task Unrelated_decision_is_recorded_and_the_pair_is_never_suggested_again()
    {
        var tag = Tag();
        var first = await SubmitAsync(tag, 9.1500, 7.5900, "Flood");
        var second = await SubmitAsync(tag, 9.1505, 7.5904, "Flood");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var relation = Assert.Single(await RelationsAsync(coordinator, second.Id), r => r.Other.Id == first.Id);

        var decide = await coordinator.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "Unrelated" }, Ct);
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ICorrelationService>().CorrelateAsync(second.Id, Ct));
        }

        var after = Assert.Single(await RelationsAsync(coordinator, second.Id), r => r.Other.Id == first.Id);
        Assert.Equal("Unrelated", after.Type);
        Assert.NotNull(after.DecidedBy);
        Assert.Equal(["ConfirmDuplicate", "Related"], after.AvailableDecisions);
    }

    [Fact]
    public async Task Confirming_a_duplicate_moves_reports_to_the_primary_and_closes_the_secondary_but_deletes_nothing()
    {
        var tag = Tag();
        var primary = await SubmitAsync(tag, 9.2500, 7.6900, "RoadAccident");
        var secondary = await SubmitAsync(tag, 9.2504, 7.6903, "RoadAccident");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var relation = Assert.Single(await RelationsAsync(coordinator, secondary.Id), r => r.Other.Id == primary.Id);

        var missingPrimary = await coordinator.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "ConfirmDuplicate" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingPrimary.StatusCode);

        var confirm = await coordinator.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "ConfirmDuplicate", primaryIncidentId = primary.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var closed = await DetailAsync(coordinator, secondary.Id);
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal($"Duplicate of {primary.Number}", closed.GetProperty("closureReason").GetString());
        Assert.Empty(closed.GetProperty("reports").EnumerateArray());

        var kept = await DetailAsync(coordinator, primary.Id);
        Assert.Equal("Reported", kept.GetProperty("status").GetString());
        var reports = kept.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => !r.GetProperty("isOriginal").GetBoolean() && r.GetProperty("reference").GetString() == secondary.Number);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var moved = await db.IncidentReports.AsNoTracking().SingleAsync(r => r.Reference == secondary.Number, Ct);
        Assert.Equal(primary.Id, moved.IncidentId);
        Assert.Equal(secondary.Id, moved.OriginalIncidentId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.ReportReassociated && a.EntityId == moved.Id.ToString(), Ct));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.RelationDecided && a.EntityId == relation.Id.ToString(), Ct));

        // The secondary's reporter still gets a status: it now follows the primary incident.
        var publicStatus = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/v1/public/reports/{secondary.Number}/status", Ct);
        Assert.Equal("Received", publicStatus.GetProperty("status").GetString());

        var final = Assert.Single(await RelationsAsync(coordinator, primary.Id), r => r.Other.Id == secondary.Id);
        Assert.Equal("ConfirmedDuplicate", final.Type);
        Assert.Equal(primary.Id, final.PrimaryIncidentId);
        Assert.Empty(final.AvailableDecisions);
    }

    [Fact]
    public async Task A_verified_incident_cannot_be_closed_as_the_duplicate()
    {
        var tag = Tag();
        var reported = await SubmitAsync(tag, 9.3500, 7.7900, "Security");
        var verified = await SubmitAsync(tag, 9.3503, 7.7902, "Security");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var reviewed = await PostAsync(coordinator, verified.Id, "review", new { version = 0 });
        await PostAsync(coordinator, verified.Id, "verify", new { approvedSeverity = "Medium", version = reviewed.GetProperty("version").GetInt32() });
        var relation = Assert.Single(await RelationsAsync(coordinator, reported.Id), r => r.Other.Id == verified.Id);
        Assert.Equal([verified.Id], relation.ConfirmablePrimaryIds);

        var wrongWay = await coordinator.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "ConfirmDuplicate", primaryIncidentId = reported.Id }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongWay.StatusCode);
        Assert.Equal("Verified", (await DetailAsync(coordinator, verified.Id)).GetProperty("status").GetString());
        Assert.Equal("PossibleDuplicate", Assert.Single(await RelationsAsync(coordinator, reported.Id), r => r.Other.Id == verified.Id).Type);

        var rightWay = await coordinator.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "ConfirmDuplicate", primaryIncidentId = verified.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, rightWay.StatusCode);
        Assert.Equal("Closed", (await DetailAsync(coordinator, reported.Id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Coordinator_can_link_incidents_manually_without_closing_either()
    {
        var a = await SubmitAsync(Tag(), 9.4500, 7.8900, "Infrastructure");
        var b = await SubmitAsync(Tag(), 12.0000, 8.5000, "Flood");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        Assert.DoesNotContain(await RelationsAsync(coordinator, a.Id), r => r.Other.Id == b.Id);

        var link = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{a.Id}/relations", new { otherIncidentNumber = b.Number.ToLowerInvariant() }, Ct);

        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        var linked = (await link.Content.ReadFromJsonAsync<Relation>(Json, Ct))!;
        Assert.Equal("Related", linked.Type);
        Assert.Equal(b.Id, linked.Other.Id);
        Assert.Null(linked.Score);
        Assert.Equal("Reported", (await DetailAsync(coordinator, a.Id)).GetProperty("status").GetString());
        Assert.Equal("Reported", (await DetailAsync(coordinator, b.Id)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{b.Id}/relations", new { otherIncidentNumber = a.Number }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{a.Id}/relations", new { otherIncidentNumber = a.Number }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{a.Id}/relations", new { otherIncidentNumber = "PH-1999-000001" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unrelated_reports_far_apart_are_not_suggested()
    {
        var a = await SubmitAsync(Tag(), 10.5000, 7.4000, "Fire");
        var b = await SubmitAsync(Tag(), 11.9000, 8.5200, "MedicalEmergency", "Man collapsed outside the pharmacy, not breathing");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        Assert.DoesNotContain(await RelationsAsync(coordinator, a.Id), r => r.Other.Id == b.Id);
    }

    [Fact]
    public async Task Only_coordinators_decide_and_responders_cannot_see_relations()
    {
        var tag = Tag();
        var first = await SubmitAsync(tag, 9.5500, 7.9900, "Fire");
        var second = await SubmitAsync(tag, 9.5502, 7.9901, "Fire");
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var relation = Assert.Single(await RelationsAsync(admin, second.Id), r => r.Other.Id == first.Id);

        Assert.Empty(relation.AvailableDecisions);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/v1/relations/{relation.Id}", new { decision = "Unrelated" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/incidents/{first.Id}/relations", new { otherIncidentNumber = second.Number }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/incidents/{first.Id}/relations", Ct)).StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Tag() => $"zq{Guid.NewGuid():N}"[..14];

    private async Task<(Guid Id, string Number)> SubmitAsync(string tag, double lat, double lon, string category, string? description = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent($"Incident at {tag} yard"), "title" },
            { new StringContent(description ?? $"Black smoke and flames at the {tag} yard gate, crowd gathering {Guid.NewGuid():N}"), "description" },
            { new StringContent(category), "category" },
            { new StringContent($"{tag} yard gate"), "locationText" },
            { new StringContent(lat.ToString(CultureInfo.InvariantCulture)), "latitude" },
            { new StringContent(lon.ToString(CultureInfo.InvariantCulture)), "longitude" },
        };
        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var number = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString()!;
        await using var scope = factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Number == number).Select(i => i.Id).SingleAsync(Ct);
        return (id, number);
    }

    private static async Task<List<Relation>> RelationsAsync(HttpClient client, Guid incidentId)
    {
        var response = await client.GetAsync($"/api/v1/incidents/{incidentId}/relations", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Relation>>(Json, Ct))!;
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct);

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record OtherIncident(Guid Id, string Number, string Status);

    private sealed record Relation(
        Guid Id, string Type, decimal? Score, string? Explanation, JsonElement? Signals, OtherIncident Other, Guid? PrimaryIncidentId,
        UserRefDto? CreatedBy, UserRefDto? DecidedBy, List<string> AvailableDecisions, List<Guid> ConfirmablePrimaryIds);
}
