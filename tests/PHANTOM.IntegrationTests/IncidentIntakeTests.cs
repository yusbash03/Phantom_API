using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;
using SkiaSharp;

namespace PHANTOM.IntegrationTests;

/// <summary>Public incident intake (Requirement 4.1–4.10).</summary>
[Collection(ApiCollection.Name)]
public sealed class IncidentIntakeTests(PhantomApiFactory factory)
{
    private const string SubmitUrl = "/api/v1/public/reports";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── 4.1 / 4.2 Valid submission ─────────────────────────────────────────────

    [Fact]
    public async Task Valid_submission_creates_report_incident_pending_analysis_and_audit_in_one_unit()
    {
        var marker = NewMarker();
        using var form = Form(marker, ("reporterName", "Ada Reporter"), ("reporterEmail", "ada@example.com"));

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.NotNull(body);
        Assert.Matches(@"^PH-\d{4}-\d{6}$", body.Reference);
        Assert.Equal($"/api/v1/public/reports/{body.Reference}/status", response.Headers.Location?.AbsolutePath);
        Assert.Equal(TimeSpan.Zero, body.SubmittedAt.Offset);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var report = await db.IncidentReports.AsNoTracking().SingleAsync(r => r.Reference == body.Reference, Ct);
        Assert.Equal(marker, report.Description);
        Assert.Equal("Ada Reporter", report.ReporterName);
        Assert.Equal("ada@example.com", report.ReporterEmail);
        Assert.Equal(report.IncidentId, report.OriginalIncidentId);

        var incident = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == report.IncidentId, Ct);
        Assert.Equal(body.Reference, incident.Number);
        Assert.Equal(IncidentStatus.Reported, incident.Status);
        Assert.Null(incident.ApprovedSeverity);
        Assert.False(incident.Location.HasCoordinates);
        Assert.True(incident.LocationNeedsVerification);

        // Created with the report (6.1); the background worker may already have picked it up.
        var analysis = await db.IncidentAnalyses.AsNoTracking().SingleAsync(a => a.ReportId == report.Id, Ct);
        Assert.Equal(1, analysis.Attempt);
        Assert.Null(analysis.RequestedById);
        Assert.True(analysis.RequestedAt <= report.SubmittedAt);
        Assert.Equal(incident.Id, analysis.IncidentId);

        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == report.Id.ToString() || a.EntityId == incident.Id.ToString() || a.EntityId == analysis.Id.ToString())
            .ToListAsync(Ct);
        Assert.Contains(audit, a => a.Action == AuditActions.ReportSubmitted && a.ActorType == AuditActorType.Anonymous);
        Assert.Contains(audit, a => a.Action == AuditActions.IncidentCreated && a.ActorType == AuditActorType.Anonymous);
        Assert.Contains(audit, a => a.Action == AuditActions.AiAnalysisQueued && a.ActorType == AuditActorType.System);
        Assert.DoesNotContain(audit, a => (a.NewValue ?? string.Empty).Contains("ada@example.com", StringComparison.Ordinal));
    }

    // ── 4.3 Invalid input persists nothing ─────────────────────────────────────

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("locationText")]
    public async Task Missing_required_field_returns_400_and_persists_nothing(string omitted)
    {
        var marker = NewMarker();
        var fields = BaseFields(marker).Where(f => f.Name != omitted).ToArray();
        using var form = BuildForm(fields);

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await HasErrorForAsync(response, omitted), $"Expected a field error for '{omitted}'.");
        await AssertNothingPersistedAsync(marker);
    }

    [Theory]
    [InlineData("91", "10", "latitude")]
    [InlineData("-90.5", "10", "latitude")]
    [InlineData("10", "180.01", "longitude")]
    [InlineData("10", "", "longitude")]
    public async Task Invalid_coordinates_return_field_error_and_persist_nothing(string latitude, string longitude, string field)
    {
        var marker = NewMarker();
        var extra = new List<(string, string)> { ("latitude", latitude) };
        if (longitude.Length > 0)
        {
            extra.Add(("longitude", longitude));
        }

        using var form = Form(marker, [.. extra]);

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await HasErrorForAsync(response, field), $"Expected a field error for '{field}'.");
        await AssertNothingPersistedAsync(marker);
    }

    [Fact]
    public async Task Unknown_category_is_rejected()
    {
        var marker = NewMarker();
        using var form = BuildForm([.. BaseFields(marker).Where(f => f.Name != "category"), ("category", "Alien")]);

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPersistedAsync(marker);
    }

    // ── 4.4 Coordinates stored as Reported and unverified ──────────────────────

    [Fact]
    public async Task Reported_coordinates_are_stored_with_reported_provenance_and_unverified()
    {
        var marker = NewMarker();
        using var form = Form(marker, ("latitude", "6.524379"), ("longitude", "3.379206"));

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var incident = await IncidentForMarkerAsync(marker);
        Assert.Equal(6.524379m, incident.Location.Latitude);
        Assert.Equal(3.379206m, incident.Location.Longitude);
        Assert.Equal(LocationProvenance.Reported, incident.Location.Provenance);
        Assert.False(incident.Location.IsVerified);
        Assert.Null(incident.Location.VerifiedById);
    }

    // ── 4.5 / 4.6 Files ────────────────────────────────────────────────────────

    [Fact]
    public async Task Disallowed_file_disguised_as_jpeg_rejects_the_submission_and_stores_nothing()
    {
        var marker = NewMarker();
        var filesBefore = StoredFileCount();
        using var form = Form(marker);
        AddFile(form, "%PDF-1.7 not really an image"u8.ToArray(), "photo.jpg", "image/jpeg");

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await HasErrorForAsync(response, "files"));
        await AssertNothingPersistedAsync(marker);
        Assert.Equal(filesBefore, StoredFileCount());
    }

    [Fact]
    public async Task One_bad_file_among_good_ones_rejects_the_whole_submission()
    {
        var marker = NewMarker();
        var filesBefore = StoredFileCount();
        using var form = Form(marker);
        AddFile(form, CreateImage(SKEncodedImageFormat.Png), "ok.png", "image/png");
        AddFile(form, "MZ\x90\0 executable"u8.ToArray(), "evil.png", "image/png");

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPersistedAsync(marker);
        Assert.Equal(filesBefore, StoredFileCount());
    }

    [Fact]
    public async Task Image_with_valid_signature_but_corrupt_body_is_rejected()
    {
        var marker = NewMarker();
        using var form = Form(marker);
        AddFile(form, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03], "broken.jpg", "image/jpeg");

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPersistedAsync(marker);
    }

    [Fact]
    public async Task More_than_five_images_are_rejected_even_when_labelled_as_audio()
    {
        var marker = NewMarker();
        using var form = Form(marker);
        for (var i = 0; i < 6; i++)
        {
            // The client-declared type is ignored; the sniffed type (image) is what counts.
            AddFile(form, CreateImage(SKEncodedImageFormat.Png), $"clip{i}.mp3", "audio/mpeg");
        }

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await HasErrorForAsync(response, "files"));
        await AssertNothingPersistedAsync(marker);
    }

    [Fact]
    public async Task Stored_image_is_reencoded_without_embedded_metadata()
    {
        const string secret = "PHANTOM-GPS-51.5074N-0.1278W";
        var marker = NewMarker();
        var jpeg = InsertExifSegment(CreateImage(SKEncodedImageFormat.Jpeg), secret);
        Assert.Contains(secret, Encoding.ASCII.GetString(jpeg), StringComparison.Ordinal);
        using var form = Form(marker);
        AddFile(form, jpeg, "../../etc/My Photo<script>.jpg", "application/octet-stream");

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var reportId = await db.IncidentReports.Where(r => r.Description == marker).Select(r => r.Id).SingleAsync(Ct);
        var attachment = await db.Attachments.AsNoTracking().SingleAsync(a => a.ReportId == reportId, Ct);
        Assert.Equal(AttachmentKind.Image, attachment.Kind);
        Assert.Equal("image/jpeg", attachment.ContentType);
        Assert.Equal("My_Photo_script_.jpg", attachment.OriginalFileName);
        Assert.Matches("^[0-9a-f]{32}$", attachment.StorageKey);

        await using var stored = await scope.ServiceProvider.GetRequiredService<IObjectStorage>().RetrieveAsync(attachment.StorageKey, Ct);
        var bytes = ((MemoryStream)stored).ToArray();
        Assert.DoesNotContain(secret, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("Exif", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(attachment.SizeBytes, bytes.Length);
        Assert.Equal(attachment.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
    }

    // ── 4.7 Idempotency ────────────────────────────────────────────────────────

    [Fact]
    public async Task Repeated_idempotency_key_returns_original_reference_without_a_second_report()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var marker = NewMarker();

        using var first = Form(marker);
        first.Headers.Add("Idempotency-Key", key);
        var r1 = await client.PostAsync(SubmitUrl, first, Ct);
        using var second = Form(marker);
        second.Headers.Add("Idempotency-Key", key.ToUpperInvariant());
        var r2 = await client.PostAsync(SubmitUrl, second, Ct);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var b1 = await r1.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        var b2 = await r2.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.Equal(b1!.Reference, b2!.Reference);
        Assert.Equal(b1.SubmittedAt, b2.SubmittedAt);
        Assert.Equal(1, await ReportCountAsync(marker));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_report()
    {
        var key = Guid.NewGuid().ToString();
        var marker = NewMarker();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var form = Form(marker);
            form.Headers.Add("Idempotency-Key", key);
            var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SubmitBody>(Ct))!.Reference;
        }));

        Assert.Single(responses.Distinct());
        Assert.Equal(1, await ReportCountAsync(marker));
    }

    [Fact]
    public async Task Malformed_idempotency_key_is_rejected()
    {
        var marker = NewMarker();
        using var form = Form(marker);
        form.Headers.Add("Idempotency-Key", "not-a-uuid");

        var response = await factory.CreateClient().PostAsync(SubmitUrl, form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingPersistedAsync(marker);
    }

    // ── 4.8 Rate limiting ──────────────────────────────────────────────────────

    [Fact]
    public void Every_public_report_endpoint_is_rate_limited()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("api/v1/public", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.Equal("public", e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName));
    }

    // ── 4.9 / 4.10 Public status ───────────────────────────────────────────────

    [Fact]
    public async Task Status_returns_only_reference_and_coarse_status()
    {
        var client = factory.CreateClient();
        using var form = Form(NewMarker());
        var submit = await client.PostAsync(SubmitUrl, form, Ct);
        var body = await submit.Content.ReadFromJsonAsync<SubmitBody>(Ct);

        var status = await client.GetAsync($"{SubmitUrl}/{body!.Reference.ToLowerInvariant()}/status", Ct);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(Ct));
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["reference", "status"], properties);
        Assert.Equal(body.Reference, json.RootElement.GetProperty("reference").GetString());
        Assert.Equal("Received", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("PH-2026-999999")]
    [InlineData("not-a-reference")]
    public async Task Status_for_unknown_reference_is_404(string reference)
    {
        var response = await factory.CreateClient().GetAsync($"{SubmitUrl}/{reference}/status", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string NewMarker() => $"Smoke rising near the market, ref {Guid.NewGuid():N}";

    private static (string Name, string Value)[] BaseFields(string marker) =>
    [
        ("title", "Fire near the market"),
        ("description", marker),
        ("category", "Fire"),
        ("locationText", "Market Road, beside the bus stop"),
    ];

    private static MultipartFormDataContent Form(string marker, params (string Name, string Value)[] extra) =>
        BuildForm([.. BaseFields(marker), .. extra]);

    private static MultipartFormDataContent BuildForm(IEnumerable<(string Name, string Value)> fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        return form;
    }

    private static void AddFile(MultipartFormDataContent form, byte[] bytes, string fileName, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "files", fileName);
    }

    private static async Task<bool> HasErrorForAsync(HttpResponseMessage response, string field)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateObject().Any(e => string.Equals(e.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AssertNothingPersistedAsync(string marker)
    {
        Assert.Equal(0, await ReportCountAsync(marker));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        Assert.False(await db.Incidents.AnyAsync(i => i.Description == marker, Ct));
    }

    private async Task<int> ReportCountAsync(string marker)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().IncidentReports.CountAsync(r => r.Description == marker, Ct);
    }

    private async Task<Incident> IncidentForMarkerAsync(string marker)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.AsNoTracking().SingleAsync(i => i.Description == marker, Ct);
    }

    private int StoredFileCount() => Directory.Exists(factory.StoragePath) ? Directory.GetFiles(factory.StoragePath).Length : 0;

    private static byte[] CreateImage(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Inserts a minimal, well-formed EXIF APP1 segment (big-endian TIFF, empty IFD) carrying <paramref name="secret"/>.</summary>
    private static byte[] InsertExifSegment(byte[] jpeg, string secret)
    {
        byte[] tiff = [(byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).Concat(Encoding.ASCII.GetBytes(secret)).ToArray();
        var length = payload.Length + 2;
        byte[] header = [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)];
        return [.. jpeg.AsSpan(0, 2), .. header, .. payload, .. jpeg.AsSpan(2)];
    }

    private sealed record SubmitBody(string Reference, DateTimeOffset SubmittedAt);
}
