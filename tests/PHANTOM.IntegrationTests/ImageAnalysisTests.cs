using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;
using SkiaSharp;

namespace PHANTOM.IntegrationTests;

/// <summary>Photos in AI analysis (Requirement 6.10): included within limits, metadata-free, and recorded.</summary>
[Collection(ApiCollection.Name)]
public sealed class ImageAnalysisTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Report_photos_reach_the_provider_without_metadata_and_the_analysis_records_it()
    {
        const string secret = "GPS 6.5244N 3.3792E";
        var (incidentId, description) = await SubmitAsync("", photos: 2, exifSecret: secret);

        var analysis = await WaitForSucceededAsync(incidentId);
        Assert.True(analysis.GetProperty("usedImageInput").GetBoolean());

        var request = Assert.Single(factory.Intelligence.Requests, r => r.Description == description);
        Assert.Equal(2, request.Images.Count);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var attachmentIds = await db.Attachments.Where(a => db.IncidentReports.Any(r => r.Id == a.ReportId && r.IncidentId == incidentId)).OrderBy(a => a.CreatedAt).Select(a => a.Id).ToListAsync(Ct);
        Assert.Equal(attachmentIds, request.Images.Select(i => i.AttachmentId));
        Assert.All(request.Images, image =>
        {
            Assert.Equal("image/jpeg", image.ContentType);
            Assert.True(image.Data.Length > 0);
            // The stored, re-encoded copy is sent, so EXIF location data never leaves PHANTOM.
            Assert.DoesNotContain(secret, Encoding.ASCII.GetString(image.Data), StringComparison.Ordinal);
        });

        var analysisId = analysis.GetProperty("id").GetString();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.AiAnalysisCompleted && a.EntityId == analysisId, Ct);
        using var recorded = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal(2, recorded.RootElement.GetProperty("imagesUsed").GetInt32());
        Assert.Equal(attachmentIds, recorded.RootElement.GetProperty("imageAttachmentIds").EnumerateArray().Select(e => e.GetGuid()));

        var staff = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var timeline = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}/timeline", Ct);
        var ai = timeline.EnumerateArray().Single(e => e.GetProperty("action").GetString() == AuditActions.AiAnalysisCompleted);
        Assert.EndsWith("Based on the report text and 2 photos.", ai.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task At_most_three_photos_are_sent()
    {
        var (incidentId, description) = await SubmitAsync("", photos: 5);
        await WaitForSucceededAsync(incidentId);
        // AnalysisService.MaxImages
        Assert.Equal(3, Assert.Single(factory.Intelligence.Requests, r => r.Description == description).Images.Count);
    }

    [Fact]
    public async Task Text_only_reports_and_providers_that_ignore_photos_are_not_recorded_as_image_input()
    {
        var (textOnly, _) = await SubmitAsync("", photos: 0);
        Assert.False((await WaitForSucceededAsync(textOnly)).GetProperty("usedImageInput").GetBoolean());

        var (ignored, description) = await SubmitAsync(ScriptedIntelligenceService.IgnoreImages, photos: 1);
        Assert.False((await WaitForSucceededAsync(ignored)).GetProperty("usedImageInput").GetBoolean());
        Assert.Single(Assert.Single(factory.Intelligence.Requests, r => r.Description == description).Images);
    }

    private async Task<JsonElement> WaitForSucceededAsync(Guid incidentId)
    {
        var staff = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var list = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}/analyses", Ct);
            var first = list.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.GetProperty("status").GetString() == "Succeeded")
            {
                return first;
            }

            Assert.True(DateTime.UtcNow < deadline, "The analysis did not succeed in time.");
            await Task.Delay(200, Ct);
        }
    }

    private async Task<(Guid IncidentId, string Description)> SubmitAsync(string marker, int photos, string? exifSecret = null)
    {
        var description = $"Warehouse fire with photos {Guid.NewGuid():N} {marker}".Trim();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Warehouse fire"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Photo site {Guid.NewGuid():N}"), "locationText" },
        };
        for (var i = 0; i < photos; i++)
        {
            var jpeg = CreateJpeg(i);
            var content = new ByteArrayContent(exifSecret is null ? jpeg : InsertExifSegment(jpeg, exifSecret));
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, "files", $"photo{i}.jpg");
        }

        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
        return (id, description);
    }

    private static byte[] CreateJpeg(int seed)
    {
        using var bitmap = new SKBitmap(8, 6);
        bitmap.Erase(new SKColor((byte)(40 * seed), 80, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] InsertExifSegment(byte[] jpeg, string secret)
    {
        byte[] tiff = [(byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).Concat(Encoding.ASCII.GetBytes(secret)).ToArray();
        var length = payload.Length + 2;
        byte[] header = [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)];
        return [.. jpeg.AsSpan(0, 2), .. header, .. payload, .. jpeg.AsSpan(2)];
    }
}
