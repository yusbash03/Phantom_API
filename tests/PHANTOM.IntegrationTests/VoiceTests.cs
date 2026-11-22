using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Voice recordings (Requirements 4.1, 6.11): transcript stored beside the original, used in analysis.</summary>
[Collection(ApiCollection.Name)]
public sealed class VoiceTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recording_is_transcribed_kept_unchanged_and_used_in_analysis_without_contact_details()
    {
        var recording = WebM("first");
        var (incidentId, description) = await SubmitAsync(recording);
        var staff = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        var analysis = await WaitForSucceededAsync(staff, incidentId);
        Assert.True(analysis.GetProperty("usedAudioTranscript").GetBoolean());

        // The prompt got the transcript with the phone number and email removed (Requirement 6.8).
        var request = Assert.Single(factory.Intelligence.Requests, r => r.Description == description);
        Assert.Contains("people are trapped upstairs", request.AudioTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("0803", request.AudioTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@example.com", request.AudioTranscript, StringComparison.Ordinal);
        Assert.Contains(TranscriptRedaction.PhoneMarker, request.AudioTranscript, StringComparison.Ordinal);
        Assert.Contains(TranscriptRedaction.EmailMarker, request.AudioTranscript, StringComparison.Ordinal);

        // Staff see the stored transcript in full, as a derived artefact beside the recording.
        var detail = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}", Ct);
        var attachment = detail.GetProperty("reports")[0].GetProperty("attachments").EnumerateArray().Single();
        Assert.Equal("Audio", attachment.GetProperty("kind").GetString());
        Assert.Equal(ScriptedTranscriptionService.Transcript, attachment.GetProperty("transcript").GetString());
        Assert.Equal(ScriptedTranscriptionService.ProviderName, attachment.GetProperty("transcriptProvider").GetString());
        Assert.NotEqual(JsonValueKind.Null, attachment.GetProperty("transcribedAt").ValueKind);

        // The original recording is untouched, and is saved under our name, not one the reporter chose.
        var response = await staff.GetAsync($"/api/v1/attachments/{attachment.GetProperty("id").GetString()}/content", Ct);
        Assert.Matches("^phantom-recording-[0-9a-f]{12}\\.webm$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(SHA256.HashData(recording), SHA256.HashData(await response.Content.ReadAsByteArrayAsync(Ct)));

        var timeline = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}/timeline", Ct);
        var transcribed = timeline.EnumerateArray().Single(e => e.GetProperty("action").GetString() == AuditActions.AttachmentTranscribed);
        Assert.Equal("Voice recording transcribed automatically", transcribed.GetProperty("summary").GetString());
        Assert.True(transcribed.GetProperty("isAi").GetBoolean());
        var ai = timeline.EnumerateArray().Single(e => e.GetProperty("action").GetString() == AuditActions.AiAnalysisCompleted);
        Assert.EndsWith("Based on the report text and the voice recording transcript.", ai.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recording_is_transcribed_once_even_when_the_analysis_is_retried()
    {
        // The first analysis fails after the recording was transcribed; the retry reuses the stored transcript.
        var (incidentId, _) = await SubmitAsync(WebM("once"), ScriptedIntelligenceService.FailOnce);
        var staff = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        await WaitForFinishedAsync(staff, incidentId, "Failed");
        var before = factory.Transcription.Calls.Count;

        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsync($"/api/v1/incidents/{incidentId}/analyses/retry", null, Ct)).StatusCode);
        var second = await WaitForSucceededAsync(staff, incidentId, attempt: 2);
        Assert.True(second.GetProperty("usedAudioTranscript").GetBoolean());
        Assert.Equal(before, factory.Transcription.Calls.Count);
    }

    [Fact]
    public async Task A_transcription_failure_never_blocks_the_analysis()
    {
        var (incidentId, description) = await SubmitAsync(WebM(ScriptedTranscriptionService.Fail));
        var staff = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);

        var analysis = await WaitForSucceededAsync(staff, incidentId);
        Assert.False(analysis.GetProperty("usedAudioTranscript").GetBoolean());
        Assert.Null(Assert.Single(factory.Intelligence.Requests, r => r.Description == description).AudioTranscript);

        var detail = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}", Ct);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("reports")[0].GetProperty("attachments")[0].GetProperty("transcript").ValueKind);
        var timeline = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}/timeline", Ct);
        var failed = timeline.EnumerateArray().Single(e => e.GetProperty("action").GetString() == AuditActions.AttachmentTranscriptionFailed);
        Assert.Equal("Voice recording could not be transcribed", failed.GetProperty("summary").GetString());
        Assert.Contains("did not respond", failed.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>A WebM EBML header (DocType "webm", as browsers record) followed by a payload carrying the test marker.</summary>
    private static byte[] WebM(string marker) =>
        [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81, 0x04, 0x42, 0xF3, 0x81, 0x08,
         0x42, 0x82, 0x84, .. "webm"u8, 0x42, 0x87, 0x81, 0x04, .. Encoding.ASCII.GetBytes($" voice note {marker} {Guid.NewGuid():N}")];

    private static Task<JsonElement> WaitForSucceededAsync(HttpClient staff, Guid incidentId, int attempt = 1) =>
        WaitForFinishedAsync(staff, incidentId, "Succeeded", attempt);

    private static async Task<JsonElement> WaitForFinishedAsync(HttpClient staff, Guid incidentId, string status, int attempt = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var list = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incidentId}/analyses", Ct);
            var match = list.EnumerateArray().FirstOrDefault(a => a.GetProperty("attempt").GetInt32() == attempt);
            if (match.ValueKind == JsonValueKind.Object && match.GetProperty("status").GetString() == status)
            {
                return match;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Analysis attempt {attempt} did not reach {status} in time.");
            await Task.Delay(200, Ct);
        }
    }

    private async Task<(Guid IncidentId, string Description)> SubmitAsync(byte[] recording, string marker = "")
    {
        var description = $"Market fire voice report {Guid.NewGuid():N} {marker}".Trim();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Market fire"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Voice site {Guid.NewGuid():N}"), "locationText" },
        };
        var file = new ByteArrayContent(recording);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "files", "voice-note.webm");

        var response = await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
        return (id, description);
    }
}
