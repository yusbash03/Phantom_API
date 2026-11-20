using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Assignments;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Domain.Responders;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Command dashboard metrics and recent activity (Requirement 12).</summary>
[Collection(ApiCollection.Name)]
public sealed class DashboardTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Metrics_are_counted_from_the_database_and_move_with_each_decision()
    {
        var (_, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var before = await SummaryAsync(coordinator);
        Assert.Equal(await CountFromDatabaseAsync(), before with { GeneratedAt = default });

        var (responderId, _) = await LinkedResponderAsync();
        var reported = await SubmitAsync($"Dashboard reported {Guid.NewGuid():N}");
        var verified = await SubmitAsync($"Dashboard verified {Guid.NewGuid():N}");
        await PostAsync(coordinator, verified, "review", new { });
        await PostAsync(coordinator, verified, "verify", new { approvedSeverity = "Critical" });
        var rejected = await SubmitAsync($"Dashboard rejected {Guid.NewGuid():N}");
        await PostAsync(coordinator, rejected, "review", new { });
        await PostAsync(coordinator, rejected, "reject", new { reason = "Test report." });

        var middle = await SummaryAsync(coordinator);
        Assert.Equal(before.TotalIncidents + 3, middle.TotalIncidents);
        Assert.Equal(before.ActiveIncidents + 2, middle.ActiveIncidents);
        Assert.Equal(before.HighPriorityActiveIncidents + 1, middle.HighPriorityActiveIncidents);
        Assert.Equal(before.AwaitingReview + 1, middle.AwaitingReview);
        Assert.Equal(before.UnassignedVerified + 1, middle.UnassignedVerified);
        Assert.Equal(before.AssignmentsAwaitingAcknowledgement, middle.AssignmentsAwaitingAcknowledgement);
        Assert.Equal(before.AvailableResponders + 1, middle.AvailableResponders);

        var assigned = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{verified}/assignments", new { responderId }, Ct);
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        var after = await SummaryAsync(coordinator);
        Assert.Equal(middle.ActiveIncidents, after.ActiveIncidents);
        Assert.Equal(middle.HighPriorityActiveIncidents, after.HighPriorityActiveIncidents);
        Assert.Equal(middle.UnassignedVerified - 1, after.UnassignedVerified);
        Assert.Equal(middle.AssignmentsAwaitingAcknowledgement + 1, after.AssignmentsAwaitingAcknowledgement);
        Assert.Equal(middle.AvailableResponders - 1, after.AvailableResponders);
        Assert.Equal(await CountFromDatabaseAsync(), after with { GeneratedAt = default });
        Assert.True(after.GeneratedAt > before.GeneratedAt);
        _ = reported;
    }

    [Fact]
    public async Task Recent_activity_is_newest_first_names_the_incident_and_leaves_out_administration()
    {
        var (coordinatorId, coordinator) = await UserClientAsync(Role.IncidentCoordinatorId);
        var incident = await SubmitAsync($"Dashboard activity {Guid.NewGuid():N}");
        await PostAsync(coordinator, incident, "review", new { });
        await PostAsync(coordinator, incident, "verify", new { approvedSeverity = "High" });
        await factory.CreateUserAsync(Role.ResponderId); // user administration is audited but is not incident activity

        var activity = (await coordinator.GetFromJsonAsync<List<Activity>>("/api/v1/dashboard/activity?limit=10", Ct))!;
        Assert.InRange(activity.Count, 1, 10);
        Assert.True(activity.Zip(activity.Skip(1)).All(p => p.First.OccurredAt >= p.Second.OccurredAt), "Newest first");
        Assert.Equal(activity.Count, activity.Select(a => a.Id).Distinct().Count());
        Assert.All(activity, a => Assert.False(string.IsNullOrEmpty(a.Incident.Number)));
        Assert.DoesNotContain(activity, a => a.Action.StartsWith("user.", StringComparison.Ordinal));

        var verify = activity.First(a => a.Incident.Id == incident && a.Summary == "Status changed from Under review to Verified");
        Assert.Equal(coordinatorId, verify.Actor?.Id);
        Assert.False(verify.IsAi);

        Assert.Equal(2, (await coordinator.GetFromJsonAsync<List<Activity>>("/api/v1/dashboard/activity?limit=2", Ct))!.Count);
        var capped = (await coordinator.GetFromJsonAsync<List<Activity>>("/api/v1/dashboard/activity?limit=500", Ct))!;
        Assert.InRange(capped.Count, 1, 50);
        // Other suites' reports can be suggested as duplicates in between, so look further back for the submission.
        var received = Assert.Single(capped, a => a.Incident.Id == incident && a.Summary.StartsWith("Public report", StringComparison.Ordinal));
        Assert.Equal("Anonymous", received.ActorType);
    }

    [Fact]
    public async Task Only_operational_staff_see_the_dashboard()
    {
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        foreach (var path in new[] { "/api/v1/dashboard/summary", "/api/v1/dashboard/activity" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync(path, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path, Ct)).StatusCode);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Summary> CountFromDatabaseAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        IncidentStatus[] ended = [IncidentStatus.Resolved, IncidentStatus.Closed, IncidentStatus.Rejected];
        var incidents = await db.Incidents.AsNoTracking().Select(i => new { i.Status, i.ApprovedSeverity }).ToListAsync(Ct);
        var active = incidents.Where(i => !ended.Contains(i.Status)).ToList();
        return new Summary(
            incidents.Count,
            active.Count,
            active.Count(i => i.ApprovedSeverity is Severity.High or Severity.Critical),
            incidents.Count(i => i.Status is IncidentStatus.Reported or IncidentStatus.UnderReview),
            incidents.Count(i => i.Status == IncidentStatus.Verified),
            await db.ResponderAssignments.CountAsync(a => a.Status == AssignmentStatus.Assigned, Ct),
            await db.Responders.CountAsync(r => r.IsActive && r.Availability == ResponderAvailability.Available, Ct),
            default);
    }

    private static async Task<Summary> SummaryAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Summary>("/api/v1/dashboard/summary", Ct))!;

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

    private static async Task PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var payload = JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["version"] = version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", payload, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private sealed record Summary(
        int TotalIncidents, int ActiveIncidents, int HighPriorityActiveIncidents, int AwaitingReview, int UnassignedVerified,
        int AssignmentsAwaitingAcknowledgement, int AvailableResponders, DateTimeOffset GeneratedAt);

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record IncidentLinkDto(Guid Id, string Number, string Title);

    private sealed record Activity(string Id, DateTimeOffset OccurredAt, string Kind, string ActorType, UserRefDto? Actor, string Action, string Summary, bool IsAi, IncidentLinkDto Incident);
}
