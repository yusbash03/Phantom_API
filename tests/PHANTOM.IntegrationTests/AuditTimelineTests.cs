using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Incident history and audit search (Requirement 15; critical property 10).</summary>
[Collection(ApiCollection.Name)]
public sealed class AuditTimelineTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Timeline_tells_the_whole_story_and_stays_readable_after_closure()
    {
        var (coordinatorId, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var incident = await SubmitAsync($"Warehouse fire {Guid.NewGuid():N}");
        await WaitForAnalysisAsync(incident);
        await PostAsync(coordinator, incident, "review", new { });
        await PostAsync(coordinator, incident, "verify", new { approvedSeverity = "High" });
        var (responderId, field) = await LinkedResponderAsync();
        var assigned = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incident}/assignments", new { responderId }, Ct);
        var assignment = (await assigned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var (action, body) in new (string, object)[]
        {
            ("acknowledge", new { }), ("status", new { status = "EnRoute" }), ("updates", new { body = "Two engines on site." }),
            ("status", new { status = "OnScene" }), ("complete", new { notes = "Fire out." }),
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignment}/{action}", body, Ct)).StatusCode);
        }

        await PostAsync(coordinator, incident, "resolve", new { resolutionSummary = "Contained by the fire service." });
        await PostAsync(coordinator, incident, "close", new { });

        var timeline = await TimelineAsync(coordinator, incident);
        string[] expectedInOrder =
        [
            "Public report PH-", "Incident PH-", "AI analysis queued", "AI recommended High severity",
            "Status changed from Reported to Under review", "Status changed from Under review to Verified",
            "assigned", "Status changed automatically from Verified to Assigned",
            "Assigned → Acknowledged", "Acknowledged → En route", "Status changed automatically from Assigned to In progress",
            "Progress update from the responder", "En route → On scene", "On scene → Completed", "Resolution notes from the responder",
            "Status changed from In progress to Resolved", "Status changed from Resolved to Closed",
        ];
        var position = -1;
        foreach (var expected in expectedInOrder)
        {
            var next = timeline.FindIndex(position + 1, e => e.Summary.Contains(expected, StringComparison.Ordinal));
            Assert.True(next > position, $"Expected “{expected}” after position {position}. Timeline: {string.Join(" | ", timeline.Select(e => e.Summary))}");
            position = next;
        }

        Assert.All(timeline.Zip(timeline.Skip(1)), pair => Assert.True(pair.First.OccurredAt <= pair.Second.OccurredAt));
        var report = timeline.First(e => e.Summary.StartsWith("Public report", StringComparison.Ordinal));
        Assert.Equal("Anonymous", report.ActorType);
        var ai = timeline.First(e => e.Summary.StartsWith("AI recommended", StringComparison.Ordinal));
        Assert.True(ai.IsAi);
        Assert.Equal("Ai", ai.Kind);
        Assert.Contains("recommendation only", ai.Detail, StringComparison.Ordinal);
        var verify = timeline.First(e => e.Summary.EndsWith("to Verified", StringComparison.Ordinal));
        Assert.False(verify.IsAi);
        Assert.Equal(coordinatorId, verify.Actor?.Id);
        Assert.Equal("Approved severity: High", verify.Detail);
        var automatic = timeline.First(e => e.Summary.Contains("automatically from Verified", StringComparison.Ordinal));
        Assert.Equal("System", automatic.ActorType);
        Assert.Equal("Because a responder was assigned.", automatic.Detail);
        Assert.Equal("Two engines on site.", timeline.Single(e => e.Kind == "Update" && e.Summary.StartsWith("Progress", StringComparison.Ordinal)).Detail);
        Assert.DoesNotContain(timeline, e => e.Action == AuditActions.AssignmentUpdatePosted);
        Assert.Equal("Resolution: Contained by the fire service.", timeline.Single(e => e.Summary.EndsWith("to Resolved", StringComparison.Ordinal)).Detail);
    }

    [Fact]
    public async Task Both_incidents_of_a_confirmed_duplicate_keep_their_history()
    {
        var (_, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var tag = $"tq{Guid.NewGuid():N}"[..14];
        var primary = await SubmitAsync($"Black smoke at the {tag} yard gate {Guid.NewGuid():N}", tag, 7.2500, 5.4900);
        var secondary = await SubmitAsync($"Black smoke at the {tag} yard gate {Guid.NewGuid():N}", tag, 7.2502, 5.4901);
        var relations = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{secondary}/relations", Ct);
        var relationId = relations.EnumerateArray().Single(r => r.GetProperty("other").GetProperty("id").GetGuid() == primary).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await coordinator.PutAsJsonAsync($"/api/v1/relations/{relationId}", new { decision = "ConfirmDuplicate", primaryIncidentId = primary }, Ct)).StatusCode);

        var primaryNumber = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{primary}", Ct)).GetProperty("number").GetString();
        var secondaryTimeline = await TimelineAsync(coordinator, secondary);
        Assert.Contains(secondaryTimeline, e => e.Summary.StartsWith("Possible duplicate of", StringComparison.Ordinal));
        Assert.Contains(secondaryTimeline, e => e.Summary.EndsWith("marked a confirmed duplicate", StringComparison.Ordinal));
        Assert.Contains(secondaryTimeline, e => e.Summary.EndsWith("to Closed", StringComparison.Ordinal) && e.Detail == $"Duplicate of: {primaryNumber}");
        Assert.Contains(secondaryTimeline, e => e.Summary.StartsWith("Public report", StringComparison.Ordinal));

        var primaryTimeline = await TimelineAsync(coordinator, primary);
        Assert.Contains(primaryTimeline, e => e.Summary.Contains("moved after a confirmed duplicate", StringComparison.Ordinal));
        Assert.Equal(2, primaryTimeline.Count(e => e.Summary.StartsWith("Public report", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Timeline_is_for_staff_only()
    {
        var incident = await SubmitAsync($"Timeline access {Guid.NewGuid():N}");
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);

        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/incidents/{incident}/timeline", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/incidents/{incident}/timeline", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/incidents/{Guid.NewGuid()}/timeline", Ct)).StatusCode);
    }

    [Fact]
    public async Task Administrators_search_the_audit_log_by_actor_entity_action_and_date()
    {
        var (adminId, admin) = await UserClientAsync(Role.AdministratorId);
        var (coordinatorId, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var incident = await SubmitAsync($"Audit search {Guid.NewGuid():N}");
        await PostAsync(coordinator, incident, "review", new { });

        var byEntity = await SearchAsync(admin, $"entityType=Incident&entityId={incident}");
        Assert.Contains(byEntity.Items, e => e.Action == AuditActions.IncidentCreated);
        Assert.Contains(byEntity.Items, e => e.Action == AuditActions.IncidentStatusChanged && e.Actor?.Id == coordinatorId);
        Assert.True(byEntity.Items.Zip(byEntity.Items.Skip(1)).All(p => p.First.Id > p.Second.Id), "Newest first");

        var byActor = await SearchAsync(admin, $"actorUserId={coordinatorId}&action={AuditActions.IncidentStatusChanged}");
        var entry = Assert.Single(byActor.Items);
        Assert.Contains("Reported", entry.PreviousValue, StringComparison.Ordinal);
        Assert.Contains("UnderReview", entry.NewValue, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(entry.CorrelationId));

        var future = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
        Assert.Empty((await SearchAsync(admin, $"actorUserId={coordinatorId}&from={Uri.EscapeDataString(future)}")).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/audit?from=2026-11-02T00:00:00Z&to=2026-11-01T00:00:00Z", Ct)).StatusCode);

        var paged = await SearchAsync(admin, "pageSize=2");
        Assert.Equal(2, paged.Items.Count);
        Assert.True(paged.TotalCount > 2);

        var facets = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit/facets", Ct);
        Assert.Contains(AuditActions.AssignmentCreated, facets.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("Incident", facets.GetProperty("entityTypes").EnumerateArray().Select(a => a.GetString()));
        _ = adminId;
    }

    [Fact]
    public async Task Only_administrators_search_and_nothing_can_change_the_audit_log()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/v1/audit", Ct)).StatusCode);

        var auditRoutes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("api/v1/audit", StringComparison.Ordinal) == true)
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            .Distinct()
            .ToList();
        Assert.Equal(["GET"], auditRoutes);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid UserId, HttpClient Client)> UserClientAsync(Guid roleId)
    {
        var user = await factory.CreateUserAsync(roleId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (user.Id, client);
    }

    private async Task<(Guid ResponderId, HttpClient Client)> LinkedResponderAsync()
    {
        var (userId, client) = await UserClientAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"TL-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Timeline Responder", team = "Test Team", skills = Array.Empty<string>(), userId }, Ct);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), client);
    }

    private async Task<Guid> SubmitAsync(string description, string? location = null, double? lat = null, double? lon = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Timeline test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent(location is null ? $"Timeline site {Guid.NewGuid():N}" : $"{location} yard gate"), "locationText" },
        };
        if (lat is not null)
        {
            form.Add(new StringContent(lat.Value.ToString(CultureInfo.InvariantCulture)), "latitude");
            form.Add(new StringContent(lon!.Value.ToString(CultureInfo.InvariantCulture)), "longitude");
        }

        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }

    private async Task WaitForAnalysisAsync(Guid incidentId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        while (!await db.IncidentAnalyses.AnyAsync(a => a.IncidentId == incidentId && a.Status == Domain.Incidents.AnalysisStatus.Succeeded, Ct))
        {
            Assert.True(DateTime.UtcNow < deadline, "The analysis did not finish.");
            await Task.Delay(200, Ct);
        }
    }

    private static async Task PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var payload = JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["version"] = version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", payload, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<List<Event>> TimelineAsync(HttpClient client, Guid incident) =>
        (await client.GetFromJsonAsync<List<Event>>($"/api/v1/incidents/{incident}/timeline", Json, Ct))!;

    private static async Task<Page> SearchAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/audit?{query}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Page>(Json, Ct))!;
    }

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record Event(string Id, DateTimeOffset OccurredAt, string Kind, string ActorType, UserRefDto? Actor, string Action, string Summary, string? Detail, bool IsAi);

    private sealed record Entry(long Id, string Action, UserRefDto? Actor, string? PreviousValue, string? NewValue, string? CorrelationId);

    private sealed record Page(List<Entry> Items, int TotalCount);
}
