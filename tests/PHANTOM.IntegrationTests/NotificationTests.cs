using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Notifications;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>In-app notifications (Requirement 14).</summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assignment_events_notify_the_responder_and_coordinators()
    {
        var (coordinatorId, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var (responderId, responderUserId, field) = await LinkedResponderAsync();

        var first = await AssignAsync(coordinator, incident, responderId);
        var assigned = await SingleAsync(responderUserId, NotificationType.AssignmentCreated, first);
        Assert.StartsWith("New assignment: PH-", assigned.Title, StringComparison.Ordinal);
        Assert.Equal(incident, assigned.IncidentId);

        Assert.Equal(HttpStatusCode.OK, (await field.PostAsJsonAsync($"/api/v1/assignments/{first}/decline", new { reason = "Vehicle in repair." }, Ct)).StatusCode);
        var declined = await SingleAsync(coordinatorId, NotificationType.AssignmentDeclined, first);
        Assert.Equal("Reason: Vehicle in repair. The incident needs another responder.", declined.Body);
        Assert.Empty(await ForAsync(responderUserId, NotificationType.AssignmentDeclined));

        var second = await AssignAsync(coordinator, incident, responderId);
        Assert.Equal(HttpStatusCode.OK, (await coordinator.PostAsJsonAsync($"/api/v1/assignments/{second}/cancel", new { reason = "Closer crew found" }, Ct)).StatusCode);
        Assert.Contains("Closer crew found", (await SingleAsync(responderUserId, NotificationType.AssignmentCancelled, second)).Body, StringComparison.Ordinal);

        var third = await AssignAsync(coordinator, incident, responderId);
        foreach (var (action, body) in new (string, object)[] { ("acknowledge", new { }), ("status", new { status = "EnRoute" }), ("status", new { status = "OnScene" }), ("complete", new { notes = "Fire out, scene safe." }) })
        {
            Assert.Equal(HttpStatusCode.OK, (await field.PostAsJsonAsync($"/api/v1/assignments/{third}/{action}", body, Ct)).StatusCode);
        }

        Assert.Contains("Fire out, scene safe.", (await SingleAsync(coordinatorId, NotificationType.AssignmentCompleted, third)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responders_without_a_linked_account_get_no_notification()
    {
        var (_, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var incident = await VerifiedIncidentAsync(coordinator);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"N-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Unlinked", team = "Test Team", skills = Array.Empty<string>() }, Ct);
        var responderId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var assignment = await AssignAsync(coordinator, incident, responderId);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Notifications
            .AnyAsync(n => n.AssignmentId == assignment && n.Type == NotificationType.AssignmentCreated, Ct));
    }

    [Fact]
    public async Task A_critical_ai_recommendation_alerts_every_coordinator_once_and_a_high_one_does_not()
    {
        var (first, _) = await UserClientAsync(Role.IncidentCoordinatorId);
        var (second, _) = await UserClientAsync(Role.IncidentCoordinatorId);
        var critical = await SubmitAsync($"Collapse, people trapped {ScriptedIntelligenceService.Critical} {Guid.NewGuid():N}");
        var high = await SubmitAsync($"Small fire {Guid.NewGuid():N}");
        await WaitForAnalysisAsync(critical);
        await WaitForAnalysisAsync(high);

        foreach (var coordinator in new[] { first, second })
        {
            var alerts = await ForAsync(coordinator, NotificationType.CriticalAiRecommendation);
            var alert = Assert.Single(alerts, n => n.IncidentId == critical);
            Assert.Contains("not a decision", alert.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(alerts, n => n.IncidentId == high);
        }
    }

    [Fact]
    public async Task A_possible_duplicate_alerts_coordinators()
    {
        var (coordinatorId, _) = await UserClientAsync(Role.IncidentCoordinatorId);
        var tag = $"nq{Guid.NewGuid():N}"[..14];
        var first = await SubmitAsync($"Black smoke at the {tag} yard gate {Guid.NewGuid():N}", tag, 8.1500, 6.4900);
        var second = await SubmitAsync($"Black smoke at the {tag} yard gate {Guid.NewGuid():N}", tag, 8.1502, 6.4901);

        // The pair may be found from either side (the second report, or the first one's AI re-run), never both.
        var alert = Assert.Single(await ForAsync(coordinatorId, NotificationType.PossibleDuplicate), n => n.IncidentId == first || n.IncidentId == second);
        Assert.Contains("Nothing is merged", alert.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Users_see_and_change_only_their_own_notifications()
    {
        var (aliceId, alice) = await UserClientAsync(Role.ResponderId);
        var (_, bob) = await UserClientAsync(Role.ResponderId);
        Guid first, second;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var n1 = Notification.Create(Guid.CreateVersion7(), aliceId, NotificationType.AssignmentCreated, "One", "b", null, null, DateTimeOffset.UtcNow.AddMinutes(-1));
            var n2 = Notification.Create(Guid.CreateVersion7(), aliceId, NotificationType.AssignmentCancelled, "Two", "b", null, null, DateTimeOffset.UtcNow);
            db.Notifications.AddRange(n1, n2);
            await db.SaveChangesAsync(Ct);
            (first, second) = (n1.Id, n2.Id);
        }

        var list = await ListAsync(alice, "");
        Assert.Equal(2, list.UnreadCount);
        Assert.Equal(["Two", "One"], list.Items.Select(n => n.Title));

        Assert.Empty((await ListAsync(bob, "")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/v1/notifications/{first}/read", null, Ct)).StatusCode);
        Assert.Equal(0, (await (await bob.PostAsync("/api/v1/notifications/read-all", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("updated").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync($"/api/v1/notifications/{first}/read", null, Ct)).StatusCode);
        var afterOne = await ListAsync(alice, "unreadOnly=true");
        Assert.Equal(1, afterOne.UnreadCount);
        Assert.Equal([second], afterOne.Items.Select(n => n.Id));

        var all = await alice.PostAsync("/api/v1/notifications/read-all", null, Ct);
        Assert.Equal(1, (await all.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("updated").GetInt32());
        Assert.Equal(0, (await ListAsync(alice, "")).UnreadCount);
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

    private async Task<(Guid ResponderId, Guid UserId, HttpClient Client)> LinkedResponderAsync()
    {
        var (userId, client) = await UserClientAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"N-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Notified Responder", team = "Test Team", skills = Array.Empty<string>(), userId }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), userId, client);
    }

    private async Task<Guid> SubmitAsync(string description, string? location = null, double? lat = null, double? lon = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Notification test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent(location is null ? $"Notification site {Guid.NewGuid():N}" : $"{location} yard gate"), "locationText" },
        };
        if (lat is not null)
        {
            form.Add(new StringContent(lat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "latitude");
            form.Add(new StringContent(lon!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "longitude");
        }

        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }

    private async Task<Guid> VerifiedIncidentAsync(HttpClient coordinator)
    {
        var id = await SubmitAsync($"Notification incident {Guid.NewGuid():N}");
        var version = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var reviewed = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{id}/review", new { version }, Ct);
        version = (await reviewed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await coordinator.PostAsJsonAsync($"/api/v1/incidents/{id}/verify", new { approvedSeverity = "High", version }, Ct)).StatusCode);
        return id;
    }

    private static async Task<Guid> AssignAsync(HttpClient coordinator, Guid incident, Guid responderId)
    {
        var response = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incident}/assignments", new { responderId }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
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

    private async Task<List<Notification>> ForAsync(Guid userId, NotificationType type)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == userId && n.Type == type).ToListAsync(Ct);
    }

    private async Task<Notification> SingleAsync(Guid userId, NotificationType type, Guid assignmentId) =>
        Assert.Single(await ForAsync(userId, type), n => n.AssignmentId == assignmentId);

    private static async Task<Page> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<Page>($"/api/v1/notifications?{query}", Json, Ct))!;

    private sealed record Item(Guid Id, string Title);

    private sealed record Page(List<Item> Items, int TotalCount, int UnreadCount);
}
