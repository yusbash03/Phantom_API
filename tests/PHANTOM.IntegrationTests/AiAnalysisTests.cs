using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>AI analysis pipeline (Requirement 6.1–6.9; critical properties 2 and 8).</summary>
[Collection(ApiCollection.Name)]
public sealed class AiAnalysisTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Submitted_report_is_analysed_and_only_the_ai_fields_change()
    {
        var (incidentId, _) = await SubmitAsync();

        var analysis = await WaitForFinishedAsync(incidentId);

        Assert.Equal("Succeeded", analysis.Status);
        Assert.Equal(1, analysis.Attempt);
        Assert.Equal("Scripted", analysis.Provider);
        Assert.Equal("scripted-1", analysis.Model);
        Assert.Equal("phantom.intel.v1", analysis.SchemaVersion);
        Assert.Matches(@"^PH-\d{4}-\d{6}$", analysis.ReportReference);
        Assert.NotNull(analysis.LatencyMs);
        Assert.NotNull(analysis.CompletedAt);
        Assert.False(analysis.IsSimulated);
        Assert.Equal("High", analysis.RecommendedSeverity);
        Assert.Equal("Scripted summary.", analysis.Result?.GetProperty("summary").GetString());

        var incident = await LoadIncidentAsync(incidentId);
        Assert.Equal(Severity.High, incident.AiRecommendedSeverity);
        Assert.Equal(IncidentCategory.Infrastructure, incident.AiRecommendedCategory);
        Assert.Null(incident.ApprovedSeverity);
        Assert.Equal(IncidentCategory.Fire, incident.Category);
        Assert.Equal(IncidentStatus.Reported, incident.Status);
        Assert.Equal(0, incident.Version);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.AiAnalysisCompleted && a.EntityId == analysis.Id.ToString(), Ct);
        Assert.Equal(AuditActorType.System, audit.ActorType);
    }

    [Fact]
    public async Task Ai_result_arriving_after_verification_never_changes_the_approved_severity()
    {
        var token = Guid.NewGuid().ToString("N");
        var (incidentId, _) = await SubmitAsync($"{ScriptedIntelligenceService.Critical} {ScriptedIntelligenceService.Hold} {token}");
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        // The coordinator decides while the analysis is still running.
        var reviewed = await PostAsync(coordinator, incidentId, "review", new { version = 0 });
        var verified = await PostAsync(coordinator, incidentId, "verify", new { approvedSeverity = "Low", version = reviewed.GetProperty("version").GetInt32() });
        factory.Intelligence.Release(token);
        var analysis = await WaitForFinishedAsync(incidentId);

        Assert.Equal("Succeeded", analysis.Status);
        var incident = await LoadIncidentAsync(incidentId);
        Assert.Equal(Severity.Low, incident.ApprovedSeverity);
        Assert.Equal(Severity.Critical, incident.AiRecommendedSeverity);
        Assert.Equal(IncidentStatus.Verified, incident.Status);

        // The AI write did not bump the version, so the coordinator's next action is not a false conflict.
        Assert.Equal(verified.GetProperty("version").GetInt32(), incident.Version);
        var resolve = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incidentId}/resolve",
            new { resolutionSummary = "Handled.", version = incident.Version }, Ct);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
    }

    [Theory]
    [InlineData(ScriptedIntelligenceService.Timeout, "Timeout")]
    [InlineData(ScriptedIntelligenceService.InvalidOutput, "InvalidOutput")]
    [InlineData(ScriptedIntelligenceService.Throws, "ProviderError")]
    public async Task Failed_analysis_leaves_report_and_incident_intact(string marker, string expectedCode)
    {
        var (incidentId, description) = await SubmitAsync(marker);

        var analysis = await WaitForFinishedAsync(incidentId);

        Assert.Equal("Failed", analysis.Status);
        Assert.Equal(expectedCode, analysis.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(analysis.FailureMessage));
        Assert.DoesNotContain("Apocalyptic", analysis.FailureMessage, StringComparison.Ordinal);
        Assert.Null(analysis.Result);

        var incident = await LoadIncidentAsync(incidentId);
        Assert.Null(incident.AiRecommendedSeverity);
        Assert.Equal(IncidentStatus.Reported, incident.Status);
        Assert.Equal(description, incident.Description);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        Assert.True(await db.IncidentReports.AnyAsync(r => r.IncidentId == incidentId && r.Description == description, Ct));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.AiAnalysisFailed && a.EntityId == analysis.Id.ToString(), Ct));
    }

    [Fact]
    public async Task Failed_analysis_can_be_retried_as_a_new_attempt()
    {
        var (incidentId, _) = await SubmitAsync(ScriptedIntelligenceService.FailOnce);
        Assert.Equal("Failed", (await WaitForFinishedAsync(incidentId)).Status);
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        var retry = await coordinator.PostAsync($"/api/v1/incidents/{incidentId}/analyses/retry", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var queued = (await retry.Content.ReadFromJsonAsync<Analysis>(Json, Ct))!;
        Assert.Equal(2, queued.Attempt);
        Assert.NotNull(queued.RequestedBy);
        var second = await WaitForFinishedAsync(incidentId, attempt: 2);
        Assert.Equal("Succeeded", second.Status);

        var all = await ListAsync(coordinator, incidentId);
        Assert.Equal([2, 1], all.Select(a => a.Attempt));
        Assert.Equal("Failed", all[1].Status);
        Assert.Equal(Severity.High, (await LoadIncidentAsync(incidentId)).AiRecommendedSeverity);

        // Only a failed latest attempt can be retried.
        var again = await coordinator.PostAsync($"/api/v1/incidents/{incidentId}/analyses/retry", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Administrators_may_retry_and_responders_may_not_see_analyses()
    {
        var (incidentId, _) = await SubmitAsync(ScriptedIntelligenceService.Timeout);
        await WaitForFinishedAsync(incidentId);
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);

        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/incidents/{incidentId}/analyses", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.PostAsync($"/api/v1/incidents/{incidentId}/analyses/retry", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/v1/incidents/{incidentId}/analyses/retry", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/incidents/{Guid.NewGuid()}/analyses", Ct)).StatusCode);
    }

    [Fact]
    public async Task Prompt_never_contains_reporter_contact_details()
    {
        var marker = Guid.NewGuid().ToString("N");
        var (incidentId, _) = await SubmitAsync(marker, form =>
        {
            form.Add(new StringContent("Contact Person Zed"), "reporterName");
            form.Add(new StringContent("+234 800 555 0101"), "reporterPhone");
            form.Add(new StringContent("zed.contact@example.com"), "reporterEmail");
        });
        await WaitForFinishedAsync(incidentId);

        var request = factory.Intelligence.Requests.Single(r => r.Description.Contains(marker, StringComparison.Ordinal));
        var sent = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("Zed", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("800 555", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analysis_stuck_in_processing_is_recovered_and_completed()
    {
        var (incidentId, _) = await SubmitAsync();
        await WaitForFinishedAsync(incidentId);

        // Simulate a worker that crashed mid-flight ten minutes ago on a second attempt.
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO incident_analyses (id, incident_id, report_id, attempt, status, schema_version, requested_at, claimed_at, used_image_input, used_audio_transcript)
                SELECT gen_random_uuid(), incident_id, report_id, 2, 'Processing', schema_version, now() - interval '11 minutes', now() - interval '10 minutes', false, false
                FROM incident_analyses WHERE incident_id = @incident AND attempt = 1
                """, connection);
            insert.Parameters.AddWithValue("incident", incidentId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
        }

        var recovered = await WaitForFinishedAsync(incidentId, attempt: 2, timeoutSeconds: 20);
        Assert.Equal("Succeeded", recovered.Status);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid IncidentId, string Description)> SubmitAsync(string marker = "", Action<MultipartFormDataContent>? extra = null)
    {
        var description = $"Smoke rising from the depot roof {Guid.NewGuid():N} {marker}".Trim();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Depot fire"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent("Depot Road"), "locationText" },
        };
        extra?.Invoke(form);
        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents
            .Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
        return (id, description);
    }

    private async Task<Incident> LoadIncidentAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
    }

    private async Task<Analysis> WaitForFinishedAsync(Guid incidentId, int attempt = 1, int timeoutSeconds = 15)
    {
        var client = await StaffAsync();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var match = (await ListAsync(client, incidentId)).FirstOrDefault(a => a.Attempt == attempt);
            if (match is { Status: "Succeeded" or "Failed" })
            {
                return match;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Analysis attempt {attempt} did not finish within {timeoutSeconds}s (last status {match?.Status ?? "missing"}).");
            await Task.Delay(200, Ct);
        }
    }

    private HttpClient? _staff;

    private async Task<HttpClient> StaffAsync() => _staff ??= await factory.CreateClientForRoleAsync(Role.AdministratorId);

    private static async Task<List<Analysis>> ListAsync(HttpClient client, Guid incidentId)
    {
        var response = await client.GetAsync($"/api/v1/incidents/{incidentId}/analyses", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Analysis>>(Json, Ct))!;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record Analysis(
        Guid Id, int Attempt, string ReportReference, string Status, string? Provider, string? Model, bool IsSimulated, string SchemaVersion,
        DateTimeOffset? CompletedAt, int? LatencyMs, string? RecommendedSeverity, JsonElement? Result, string? FailureCode, string? FailureMessage,
        UserRefDto? RequestedBy);
}
