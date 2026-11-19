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
using SkiaSharp;

namespace PHANTOM.IntegrationTests;

/// <summary>Incident list, detail, edits and coordinator transitions (Requirement 5).</summary>
[Collection(ApiCollection.Name)]
public sealed class IncidentManagementTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Authorisation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Responders_cannot_read_incidents_and_administrators_cannot_decide_them()
    {
        var incident = await SubmitAsync();
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);

        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync("/api/v1/incidents", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/incidents/{incident.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/incidents/{incident.Id}", Ct)).StatusCode);

        var adminReview = await admin.PostAsJsonAsync($"/api/v1/incidents/{incident.Id}/review", new { version = incident.Version }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, adminReview.StatusCode);
        var adminView = await GetAsync(admin, incident.Id);
        Assert.Empty(adminView.AvailableActions);
        Assert.Equal("Reported", adminView.Status);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coordinator_drives_full_lifecycle_and_every_step_is_audited()
    {
        var (client, coordinatorId) = await CoordinatorAsync();
        var incident = await SubmitAsync();
        Assert.Equal(["Edit", "BeginReview", "Reject"], (await GetAsync(client, incident.Id)).AvailableActions);

        var reviewed = await PostAsync(client, incident, "review", new { });
        Assert.Equal("UnderReview", reviewed.Status);
        Assert.Equal(coordinatorId, reviewed.AssignedCoordinator?.Id);
        Assert.Contains("Verify", reviewed.AvailableActions);

        var verified = await PostAsync(client, reviewed, "verify", new { approvedSeverity = "High" });
        Assert.Equal("Verified", verified.Status);
        Assert.Equal("High", verified.ApprovedSeverity);
        Assert.Equal(coordinatorId, verified.VerifiedBy?.Id);
        Assert.NotNull(verified.VerifiedAt);
        Assert.Contains("ChangeSeverity", verified.AvailableActions);

        var resolved = await PostAsync(client, verified, "resolve", new { resolutionSummary = "Fire extinguished by the local brigade." });
        Assert.Equal("Resolved", resolved.Status);
        Assert.Equal("Fire extinguished by the local brigade.", resolved.ResolutionSummary);
        Assert.Equal(coordinatorId, resolved.ResolvedBy?.Id);

        var reopened = await PostAsync(client, resolved, "reopen", new { reason = "Flare-up reported." });
        Assert.Equal("InProgress", reopened.Status);
        Assert.Null(reopened.ResolvedAt);

        var resolvedAgain = await PostAsync(client, reopened, "resolve", new { resolutionSummary = "Site cooled and secured." });
        var closed = await PostAsync(client, resolvedAgain, "close", new { });
        Assert.Equal("Closed", closed.Status);
        Assert.NotNull(closed.ClosedAt);
        Assert.Empty(closed.AvailableActions);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var transitions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == incident.Id.ToString() && a.Action == AuditActions.IncidentStatusChanged)
            .OrderBy(a => a.Id)
            .ToListAsync(Ct);
        Assert.Equal(6, transitions.Count);
        Assert.All(transitions, a => Assert.Equal(coordinatorId, a.ActorUserId));
        Assert.Contains("\"Verify\"", transitions[1].Metadata, StringComparison.Ordinal);
        Assert.Contains("\"High\"", transitions[1].Metadata, StringComparison.Ordinal);
        Assert.Contains("Flare-up reported.", transitions[3].Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transition_outside_the_table_is_rejected_and_changes_nothing()
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{incident.Id}/verify", new { approvedSeverity = "Low", version = incident.Version }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var after = await GetAsync(client, incident.Id);
        Assert.Equal("Reported", after.Status);
        Assert.Null(after.ApprovedSeverity);
        Assert.Equal(incident.Version, after.Version);
    }

    [Theory]
    [InlineData("verify", "{}", "approvedSeverity")]
    [InlineData("reject", "{\"reason\":\"   \"}", "reason")]
    [InlineData("resolve", "{\"resolutionSummary\":\"\"}", "resolutionSummary")]
    public async Task Decisions_without_required_details_are_rejected(string action, string extraJson, string field)
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();
        if (action != "reject")
        {
            incident = await PostAsync(client, incident, "review", new { });
        }

        if (action == "resolve")
        {
            incident = await PostAsync(client, incident, "verify", new { approvedSeverity = "Medium" });
        }

        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(extraJson)!;
        body["version"] = incident.Version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{incident.Id}/{action}", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"Expected a field error for {field}.");
        Assert.Equal(incident.Status, (await GetAsync(client, incident.Id)).Status);
    }

    [Fact]
    public async Task Reject_records_the_reason()
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();

        var rejected = await PostAsync(client, incident, "reject", new { reason = "Prank report; caller confirmed." });

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Prank report; caller confirmed.", rejected.ClosureReason);
    }

    // ── Edits and severity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_changes_the_incident_but_never_the_original_report_and_is_audited()
    {
        var (client, coordinatorId) = await CoordinatorAsync();
        var incident = await SubmitAsync();
        var original = incident.Reports.Single();

        var response = await client.PatchAsJsonAsync($"/api/v1/incidents/{incident.Id}",
            new { title = "Warehouse fire, Dock 4", description = "Confirmed structure fire.", category = "Infrastructure", version = incident.Version }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = (await response.Content.ReadFromJsonAsync<Detail>(Json, Ct))!;
        Assert.Equal("Warehouse fire, Dock 4", edited.Title);
        Assert.Equal("Infrastructure", edited.Category);
        Assert.NotEqual(incident.Version, edited.Version);
        var report = edited.Reports.Single();
        Assert.Equal(original.Title, report.Title);
        Assert.Equal(original.Description, report.Description);
        Assert.Equal("Fire", report.Category);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityId == incident.Id.ToString() && a.Action == AuditActions.IncidentUpdated, Ct);
        Assert.Equal(coordinatorId, audit.ActorUserId);
        Assert.Contains(original.Title, audit.PreviousValue, StringComparison.Ordinal);
        Assert.Contains("Warehouse fire, Dock 4", audit.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_without_category_is_rejected_rather_than_defaulted()
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();

        var response = await client.PatchAsJsonAsync($"/api/v1/incidents/{incident.Id}",
            new { title = "New title", description = "New description", version = incident.Version }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Fire", (await GetAsync(client, incident.Id)).Category);
    }

    [Fact]
    public async Task Approved_severity_can_only_be_changed_after_verification_and_is_audited()
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();

        var early = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}/severity", new { severity = "Critical", version = incident.Version }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        var verified = await PostAsync(client, await PostAsync(client, incident, "review", new { }), "verify", new { approvedSeverity = "Low" });
        var response = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}/severity", new { severity = "Critical", version = verified.Version }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Critical", (await response.Content.ReadFromJsonAsync<Detail>(Json, Ct))!.ApprovedSeverity);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs
            .AnyAsync(a => a.EntityId == incident.Id.ToString() && a.Action == AuditActions.IncidentSeverityChanged, Ct));
    }

    // ── 5.10 Concurrency ───────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_version_is_a_conflict_and_is_not_applied()
    {
        var (client, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();
        await PostAsync(client, incident, "review", new { });

        var stale = await client.PostAsJsonAsync($"/api/v1/incidents/{incident.Id}/reject", new { reason = "Duplicate", version = incident.Version }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("UnderReview", (await GetAsync(client, incident.Id)).Status);
    }

    [Fact]
    public async Task Concurrent_edits_from_the_same_version_let_exactly_one_win()
    {
        var (first, _) = await CoordinatorAsync();
        var (second, _) = await CoordinatorAsync();
        var incident = await SubmitAsync();

        var responses = await Task.WhenAll(
            first.PatchAsJsonAsync($"/api/v1/incidents/{incident.Id}", new { title = "Edit A", description = "A", category = "Fire", version = incident.Version }, Ct),
            second.PatchAsJsonAsync($"/api/v1/incidents/{incident.Id}", new { title = "Edit B", description = "B", category = "Flood", version = incident.Version }, Ct));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    // ── 5.11 List ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_filters_by_status_category_severity_and_search_and_sorts_by_severity()
    {
        var (client, _) = await CoordinatorAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var low = await SubmitAsync(tag, "Flood");
        var critical = await SubmitAsync(tag, "Flood");
        var untouched = await SubmitAsync(tag, "Security");
        await PostAsync(client, await PostAsync(client, low, "review", new { }), "verify", new { approvedSeverity = "Low" });
        await PostAsync(client, await PostAsync(client, critical, "review", new { }), "verify", new { approvedSeverity = "Critical" });

        var verified = await ListAsync(client, $"search={tag}&status=Verified&sort=Severity&direction=Desc");
        Assert.Equal([critical.Id, low.Id], verified.Items.Select(i => i.Id));
        Assert.Equal(2, verified.TotalCount);

        var bySeverity = await ListAsync(client, $"search={tag}&severity=Critical");
        Assert.Equal(critical.Id, Assert.Single(bySeverity.Items).Id);

        var byCategory = await ListAsync(client, $"search={tag}&category=Security");
        var item = Assert.Single(byCategory.Items);
        Assert.Equal(untouched.Id, item.Id);
        Assert.Equal(1, item.ReportCount);
        Assert.False(item.LocationIsVerified);

        var byNumber = await ListAsync(client, $"search={untouched.Number.ToLowerInvariant()}");
        Assert.Equal(untouched.Id, Assert.Single(byNumber.Items).Id);

        var paged = await ListAsync(client, $"search={tag}&pageSize=2&page=2");
        Assert.Single(paged.Items);
        Assert.Equal(3, paged.TotalCount);

        var future = await ListAsync(client, $"search={tag}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}");
        Assert.Empty(future.Items);
    }

    [Fact]
    public async Task List_rejects_an_inverted_date_range()
    {
        var (client, _) = await CoordinatorAsync();
        var response = await client.GetAsync($"/api/v1/incidents?from=2026-11-02T00:00:00Z&to=2026-11-01T00:00:00Z", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Detail and attachments ─────────────────────────────────────────────────

    [Fact]
    public async Task Detail_includes_reporter_details_and_attachments_downloadable_only_by_staff()
    {
        var (client, _) = await CoordinatorAsync();
        var image = CreatePng();
        var incident = await SubmitAsync(extra: form =>
        {
            form.Add(new StringContent("Ada Reporter"), "reporterName");
            form.Add(new StringContent("+234 800 000 0000"), "reporterPhone");
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files", "scene.png");
        });

        var report = Assert.Single(incident.Reports);
        Assert.True(report.IsOriginal);
        Assert.Equal("Ada Reporter", report.ReporterName);
        Assert.Equal("+234 800 000 0000", report.ReporterPhone);
        var attachment = Assert.Single(report.Attachments);
        Assert.Equal("scene.png", attachment.OriginalFileName);
        Assert.Equal("Image", attachment.Kind);

        var download = await client.GetAsync($"/api/v1/attachments/{attachment.Id}/content", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(attachment.SizeBytes, (await download.Content.ReadAsByteArrayAsync(Ct)).Length);

        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/attachments/{attachment.Id}/content", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/attachments/{Guid.NewGuid()}/content", Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_incident_is_404()
    {
        var (client, _) = await CoordinatorAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/incidents/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(HttpClient Client, Guid UserId)> CoordinatorAsync()
    {
        var user = await factory.CreateUserAsync(Role.IncidentCoordinatorId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (client, user.Id);
    }

    private async Task<Detail> SubmitAsync(string? tag = null, string category = "Fire", Action<MultipartFormDataContent>? extra = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent($"Smoke at warehouse {tag}".Trim()), "title" },
            { new StringContent($"Thick smoke from the roof {Guid.NewGuid():N}"), "description" },
            { new StringContent(category), "category" },
            { new StringContent("Dock Road"), "locationText" },
        };
        extra?.Invoke(form);
        var submit = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var reference = (await submit.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reference").GetString();

        await using var scope = factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents
            .Where(i => i.Number == reference).Select(i => i.Id).SingleAsync(Ct);
        var (client, _) = await CoordinatorAsync();
        return await GetAsync(client, id);
    }

    private static async Task<Detail> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/incidents/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Detail>(Json, Ct))!;
    }

    private static async Task<Detail> PostAsync(HttpClient client, Detail incident, string action, object body)
    {
        var payload = JsonSerializer.SerializeToElement(body, Json).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["version"] = incident.Version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{incident.Id}/{action}", payload, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<Detail>(Json, Ct))!;
    }

    private static async Task<Page> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/incidents?{query}", Ct);
        Assert.True(response.IsSuccessStatusCode, $"List returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<Page>(Json, Ct))!;
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(3, 3);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record AttachmentDto(Guid Id, string Kind, string ContentType, long SizeBytes, string OriginalFileName);

    private sealed record ReportDto(Guid Id, string Reference, bool IsOriginal, string Title, string Description, string Category,
        string? ReporterName, string? ReporterPhone, string? ReporterEmail, List<AttachmentDto> Attachments);

    private sealed record Detail(
        Guid Id, string Number, string Title, string Description, string Category, string Status, string? ApprovedSeverity,
        UserRefDto? AssignedCoordinator, UserRefDto? VerifiedBy, DateTimeOffset? VerifiedAt, string? ResolutionSummary,
        UserRefDto? ResolvedBy, DateTimeOffset? ResolvedAt, string? ClosureReason, DateTimeOffset? ClosedAt, int Version,
        List<string> AvailableActions, List<ReportDto> Reports);

    private sealed record ListItem(Guid Id, string Number, int ReportCount, bool LocationIsVerified);

    private sealed record Page(List<ListItem> Items, int Page_, int PageSize, int TotalCount);
}
