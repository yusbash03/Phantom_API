using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Assignment workflow (Requirement 11; critical properties 5 and 7).</summary>
[Collection(ApiCollection.Name)]
public sealed class AssignmentTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_assignment_lifecycle_drives_incident_and_responder_state()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var (responderId, field) = await LinkedResponderAsync();

        var assignment = await AssignAsync(coordinator, incident, responderId);
        Assert.Equal("Assigned", assignment.Status);
        Assert.Equal(["Cancel"], assignment.AvailableActions);
        Assert.Equal("Assigned", await IncidentStatusAsync(coordinator, incident));
        Assert.Equal("Assigned", await ResponderAvailabilityAsync(responderId));

        var mine = await field.GetFromJsonAsync<List<Assignment>>("/api/v1/assignments/mine", Json, Ct);
        var listed = Assert.Single(mine!);
        Assert.Equal(["Acknowledge", "Decline"], listed.AvailableActions);
        Assert.False(string.IsNullOrEmpty(listed.Incident.Description));

        await ActAsync(field, assignment.Id, "acknowledge", null, "Acknowledged");
        var enRoute = await ActAsync(field, assignment.Id, "status", new { status = "EnRoute" }, "EnRoute");
        Assert.NotNull(enRoute.EnRouteAt);
        Assert.Equal("InProgress", await IncidentStatusAsync(coordinator, incident));

        var updated = await ActAsync(field, assignment.Id, "updates", new { body = "Two casualties treated, fire contained." }, "EnRoute");
        Assert.Equal("ProgressUpdate", Assert.Single(updated.Updates).Kind);
        await ActAsync(field, assignment.Id, "status", new { status = "OnScene" }, "OnScene");
        var completed = await ActAsync(field, assignment.Id, "complete", new { notes = "Scene handed to the fire service." }, "Completed");

        Assert.Equal("Scene handed to the fire service.", completed.CompletionNotes);
        Assert.Contains(completed.Updates, u => u.Kind == "ResolutionNote");
        Assert.Empty(completed.AvailableActions);
        Assert.Equal("Available", await ResponderAvailabilityAsync(responderId));
        // Requirement 11.8: completing does not resolve the incident; a coordinator does.
        Assert.Equal("InProgress", await IncidentStatusAsync(coordinator, incident));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var systemTransitions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == incident.ToString() && a.Action == AuditActions.IncidentStatusChanged && a.ActorType == AuditActorType.System)
            .OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Equal(2, systemTransitions.Count);
        Assert.Contains("FirstAssignmentCreated", systemTransitions[0].Metadata, StringComparison.Ordinal);
        Assert.Contains("ResponderEnRoute", systemTransitions[1].Metadata, StringComparison.Ordinal);
        Assert.Contains(assignment.Id.ToString(), systemTransitions[1].Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_assigned_responder_can_act_and_everyone_else_gets_404()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var (responderId, _) = await LinkedResponderAsync();
        var (_, otherResponder) = await LinkedResponderAsync();
        var assignment = await AssignAsync(coordinator, incident, responderId);

        foreach (var (action, body) in new (string, object?)[]
        {
            ("acknowledge", null), ("decline", new { reason = "No" }), ("status", new { status = "EnRoute" }),
            ("updates", new { body = "x" }), ("complete", new { notes = "x" }),
        })
        {
            var response = await otherResponder.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/{action}", body ?? new { }, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{action} returned {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await otherResponder.GetAsync($"/api/v1/assignments/{assignment.Id}", Ct)).StatusCode);
        Assert.Empty((await otherResponder.GetFromJsonAsync<List<Assignment>>("/api/v1/assignments/mine", Json, Ct))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/acknowledge", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await coordinator.GetAsync($"/api/v1/assignments/{assignment.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Assigning_requires_an_assignable_incident_and_an_override_for_unsuitable_responders()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var (responderId, _) = await LinkedResponderAsync();

        var reported = await SubmitAsync();
        var early = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{reported}/assignments", new { responderId }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        var first = await VerifiedIncidentAsync(coordinator);
        await AssignAsync(coordinator, first, responderId);
        var duplicate = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{first}/assignments", new { responderId }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var second = await VerifiedIncidentAsync(coordinator);
        var busy = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{second}/assignments", new { responderId }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("override", await busy.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        var overridden = await AssignAsync(coordinator, second, responderId, overrideReason: "Only medic within 5 km.");
        Assert.Equal("Only medic within 5 km.", overridden.OverrideReason);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
                .SingleAsync(a => a.Action == AuditActions.AssignmentCreated && a.EntityId == overridden.Id.ToString(), Ct);
            Assert.Contains("already on another active assignment", audit.Metadata, StringComparison.Ordinal);
        }

        var (unavailableId, unavailableClient) = await LinkedResponderAsync();
        Assert.Equal(HttpStatusCode.OK, (await unavailableClient.PutAsJsonAsync("/api/v1/responders/me/availability", new { availability = "Unavailable" }, Ct)).StatusCode);
        var third = await VerifiedIncidentAsync(coordinator);
        Assert.Equal(HttpStatusCode.Conflict, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{third}/assignments", new { responderId = unavailableId }, Ct)).StatusCode);

        var (inactiveId, _) = await LinkedResponderAsync();
        var inactive = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/responders/{inactiveId}", Ct);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/responders/{inactiveId}/deactivate", new { version = inactive.GetProperty("version").GetInt32() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{third}/assignments", new { responderId = inactiveId }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Declining_or_cancelling_frees_the_responder_and_reverts_an_unstarted_incident()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var (responderId, field) = await LinkedResponderAsync();

        var first = await AssignAsync(coordinator, incident, responderId);
        Assert.Equal(HttpStatusCode.BadRequest, (await field.PostAsJsonAsync($"/api/v1/assignments/{first.Id}/decline", new { reason = "" }, Ct)).StatusCode);
        var declined = await ActAsync(field, first.Id, "decline", new { reason = "Off shift, vehicle in repair." }, "Declined");
        Assert.Equal("Off shift, vehicle in repair.", declined.EndedReason);
        Assert.Equal("Verified", await IncidentStatusAsync(coordinator, incident));
        Assert.Equal("Available", await ResponderAvailabilityAsync(responderId));

        var second = await AssignAsync(coordinator, incident, responderId);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.PostAsJsonAsync($"/api/v1/assignments/{second.Id}/cancel", new { reason = " " }, Ct)).StatusCode);
        var cancel = await coordinator.PostAsJsonAsync($"/api/v1/assignments/{second.Id}/cancel", new { reason = "Closer team found." }, Ct);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Verified", await IncidentStatusAsync(coordinator, incident));
        Assert.Equal("Available", await ResponderAvailabilityAsync(responderId));

        var history = await coordinator.GetFromJsonAsync<List<Assignment>>($"/api/v1/incidents/{incident}/assignments", Json, Ct);
        Assert.Equal(["Cancelled", "Declined"], history!.Select(a => a.Status));
    }

    [Fact]
    public async Task Assignment_steps_follow_the_allowed_order()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var (responderId, field) = await LinkedResponderAsync();
        var assignment = await AssignAsync(coordinator, incident, responderId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/updates", new { body = "early" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/status", new { status = "OnScene" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/status", new { status = "Completed" }, Ct)).StatusCode);
        await ActAsync(field, assignment.Id, "acknowledge", null, "Acknowledged");
        await ActAsync(field, assignment.Id, "status", new { status = "EnRoute" }, "EnRoute");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/decline", new { reason = "Too late" }, Ct)).StatusCode);

        // Availability cannot be changed by hand during an active assignment (10.4).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await field.PutAsJsonAsync("/api/v1/responders/me/availability", new { availability = "Unavailable" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Incident_list_filters_by_whether_an_assignment_is_active()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var assigned = await VerifiedIncidentAsync(coordinator);
        var unassigned = await VerifiedIncidentAsync(coordinator);
        var (responderId, _) = await LinkedResponderAsync();
        await AssignAsync(coordinator, assigned, responderId);

        async Task<List<Guid>> Ids(string filter) =>
            (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents?status=Assigned&status=Verified&pageSize=100&hasActiveAssignment={filter}", Ct))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(assigned, await Ids("true"));
        Assert.DoesNotContain(unassigned, await Ids("true"));
        Assert.Contains(unassigned, await Ids("false"));
        Assert.DoesNotContain(assigned, await Ids("false"));
    }

    [Fact]
    public async Task A_critical_ai_recommendation_never_creates_an_assignment()
    {
        var description = $"Building collapse, people trapped {ScriptedIntelligenceService.Critical} {Guid.NewGuid():N}";
        var incident = await SubmitAsync(description);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        while (!await db.IncidentAnalyses.AnyAsync(a => a.IncidentId == incident && a.Status == Domain.Incidents.AnalysisStatus.Succeeded, Ct))
        {
            Assert.True(DateTime.UtcNow < deadline, "The analysis did not finish.");
            await Task.Delay(200, Ct);
        }

        Assert.Equal(Domain.Incidents.Severity.Critical, (await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident, Ct)).AiRecommendedSeverity);
        Assert.False(await db.ResponderAssignments.AnyAsync(a => a.IncidentId == incident, Ct));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SubmitAsync(string? description = null)
    {
        description ??= $"Assignment test report {Guid.NewGuid():N}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Assignment test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Assignment test site {Guid.NewGuid():N}"), "locationText" },
        };
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }

    private async Task<Guid> VerifiedIncidentAsync(HttpClient coordinator)
    {
        var id = await SubmitAsync();
        var version = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var reviewed = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{id}/review", new { version }, Ct);
        version = (await reviewed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt32();
        var verified = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{id}/verify", new { approvedSeverity = "High", version }, Ct);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return id;
    }

    /// <summary>A responder profile linked to a fresh Responder account, and that account's client.</summary>
    private async Task<(Guid ResponderId, HttpClient Client)> LinkedResponderAsync()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"A-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Field Responder", team = "Test Team", skills = Array.Empty<string>(), userId = user.Id }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), client);
    }

    private static async Task<Assignment> AssignAsync(HttpClient coordinator, Guid incident, Guid responderId, string? overrideReason = null)
    {
        var response = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incident}/assignments", new { responderId, overrideReason }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<Assignment>(Json, Ct))!;
    }

    private static async Task<Assignment> ActAsync(HttpClient client, Guid id, string action, object? body, string expectedStatus)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/assignments/{id}/{action}", body ?? new { }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var assignment = (await response.Content.ReadFromJsonAsync<Assignment>(Json, Ct))!;
        Assert.Equal(expectedStatus, assignment.Status);
        return assignment;
    }

    private static async Task<string> IncidentStatusAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("status").GetString()!;

    private async Task<string> ResponderAvailabilityAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Responders.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).Availability.ToString();
    }

    private sealed record UpdateDto(string Kind, string Body);

    private sealed record IncidentDto(Guid Id, string Number, string Description);

    private sealed record Assignment(
        Guid Id, string Status, IncidentDto Incident, DateTimeOffset? EnRouteAt, string? EndedReason, string? OverrideReason,
        string? CompletionNotes, List<UpdateDto> Updates, List<string> AvailableActions);
}
