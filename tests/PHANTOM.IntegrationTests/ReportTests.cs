using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Summary reports and CSV export (Requirement 16), checked against a known set of incidents.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReportTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_summary_counts_times_and_ai_agreement_for_exactly_the_period()
    {
        var from = DateTimeOffset.UtcNow.AddSeconds(-1);
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var (responderId, field) = await LinkedResponderAsync();

        // The scripted AI recommends High for every report.
        var agreed = await SubmitAndAnalyseAsync();     // approved High: agrees; assigned, acknowledged, resolved
        var aiHigher = await SubmitAndAnalyseAsync();   // approved Low: the AI was higher
        var untouched = await SubmitAndAnalyseAsync();  // still Reported: not compared
        await PostAsync(coordinator, agreed, "review", new { });
        await PostAsync(coordinator, agreed, "verify", new { approvedSeverity = "High" });
        await PostAsync(coordinator, aiHigher, "review", new { });
        await PostAsync(coordinator, aiHigher, "verify", new { approvedSeverity = "Low" });
        var assigned = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{agreed}/assignments", new { responderId }, Ct);
        var assignmentId = (await assigned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await field.PostAsJsonAsync($"/api/v1/assignments/{assignmentId}/acknowledge", new { }, Ct)).StatusCode);
        await PostAsync(coordinator, agreed, "resolve", new { resolutionSummary = "Handled." });
        var to = DateTimeOffset.UtcNow.AddSeconds(1);

        var summary = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/reports/summary?{Range(from, to)}", Ct);

        Assert.Equal(3, summary.GetProperty("totalIncidents").GetInt32());
        Assert.Equal(3, Count(summary, "byCategory", "Fire"));
        Assert.Equal(0, Count(summary, "byCategory", "Flood"));
        Assert.Equal(1, Count(summary, "bySeverity", "High"));
        Assert.Equal(1, Count(summary, "bySeverity", "Low"));
        Assert.Equal(1, Count(summary, "bySeverity", "NotAssessed"));
        Assert.Equal(1, Count(summary, "byStatus", "Resolved"));
        Assert.Equal(1, Count(summary, "byStatus", "Verified"));
        Assert.Equal(1, Count(summary, "byStatus", "Reported"));

        Assert.Equal(2, summary.GetProperty("reportToVerification").GetProperty("sampleSize").GetInt32());
        Assert.Equal(1, summary.GetProperty("reportToFirstAcknowledgement").GetProperty("sampleSize").GetInt32());
        Assert.Equal(1, summary.GetProperty("reportToResolution").GetProperty("sampleSize").GetInt32());
        Assert.True(summary.GetProperty("reportToVerification").GetProperty("medianSeconds").GetDouble() >= 0);

        // Medians match the stored timestamps.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var resolved = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == agreed, Ct);
            var expected = Math.Round((resolved.ResolvedAt!.Value - resolved.CreatedAt).TotalSeconds, 1);
            Assert.Equal(expected, summary.GetProperty("reportToResolution").GetProperty("medianSeconds").GetDouble(), 1);
        }

        var ai = summary.GetProperty("aiSeverityAgreement");
        Assert.Equal(2, ai.GetProperty("compared").GetInt32());
        Assert.Equal(1, ai.GetProperty("agreed").GetInt32());
        Assert.Equal(1, ai.GetProperty("aiHigher").GetInt32());
        Assert.Equal(0, ai.GetProperty("aiLower").GetInt32());
        Assert.Equal(0.5, ai.GetProperty("rate").GetDouble());
        _ = untouched;

        // An empty period has no medians and no agreement rate rather than zeros.
        var empty = await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/reports/summary?{Range(from.AddYears(-5), from.AddYears(-5).AddDays(1))}", Ct);
        Assert.Equal(0, empty.GetProperty("totalIncidents").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("reportToResolution").GetProperty("medianSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("aiSeverityAgreement").GetProperty("rate").ValueKind);
    }

    [Fact]
    public async Task The_csv_export_has_one_row_per_figure_and_downloads_as_a_file()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = DateTimeOffset.UtcNow.AddMinutes(1);

        var response = await admin.GetAsync($"/api/v1/reports/summary.csv?{Range(from, to)}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("phantom-summary-2026-11-01-to-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("section,item,value,sample_size", lines[0]);
        Assert.Contains("period,from_utc,2026-11-01T00:00:00.0000000Z,", lines);
        Assert.Contains(lines, l => l.StartsWith("incidents,total,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("category,Fire,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("approved_severity,NotAssessed,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("median_seconds,report_to_first_acknowledgement,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("ai_severity_agreement,rate,", StringComparison.Ordinal));

        var json = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/summary?{Range(from, to)}", Ct);
        Assert.Contains($"incidents,total,{json.GetProperty("totalIncidents").GetInt32()},", lines);
    }

    [Fact]
    public async Task Periods_are_validated_and_only_staff_can_see_reports()
    {
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.GetAsync("/api/v1/reports/summary", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.GetAsync($"/api/v1/reports/summary?{Range(now, now.AddDays(-1))}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.GetAsync($"/api/v1/reports/summary?{Range(now.AddDays(-400), now)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await coordinator.GetAsync($"/api/v1/reports/summary.csv?{Range(now, now.AddDays(-1))}", Ct)).StatusCode);

        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/v1/reports/summary?{Range(now.AddDays(-1), now)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/reports/summary.csv?{Range(now.AddDays(-1), now)}", Ct)).StatusCode);
    }

    private static string Range(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";

    private static int Count(JsonElement summary, string list, string key) =>
        summary.GetProperty(list).EnumerateArray().Single(i => i.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    private async Task<Guid> SubmitAndAnalyseAsync()
    {
        var description = $"Report summary fire {Guid.NewGuid():N}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Summary test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Summary site {Guid.NewGuid():N}"), "locationText" },
        };
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var id = await db.Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await db.Incidents.AnyAsync(i => i.Id == id && i.AiRecommendedSeverity != null, Ct))
        {
            Assert.True(DateTime.UtcNow < deadline, "The analysis did not finish.");
            await Task.Delay(200, Ct);
        }

        return id;
    }

    private async Task<(Guid ResponderId, HttpClient Client)> LinkedResponderAsync()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"RP-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Report Responder", team = "Test Team", skills = Array.Empty<string>(), userId = user.Id }, Ct);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), client);
    }

    private static async Task PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var payload = JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["version"] = version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", payload, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }
}
