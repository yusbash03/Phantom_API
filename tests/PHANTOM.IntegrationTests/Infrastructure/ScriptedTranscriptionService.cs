using System.Collections.Concurrent;
using System.Text;
using PHANTOM.Application.Intelligence;

namespace PHANTOM.IntegrationTests.Infrastructure;

/// <summary>In-process speech-to-text for tests. A recording whose bytes contain "[stt:fail]" fails.</summary>
public sealed class ScriptedTranscriptionService : ITranscriptionService
{
    public const string Fail = "[stt:fail]";
    public const string ProviderName = "Scripted STT";
    public const string Transcript = "There is a fire at the market and people are trapped upstairs. Call me back on 0803 123 4567 or ada@example.com.";

    public ConcurrentQueue<(string ContentType, int Length)> Calls { get; } = new();

    public bool IsConfigured => true;

    public Task<TranscriptionOutcome> TranscribeAsync(string contentType, byte[] audio, CancellationToken cancellationToken)
    {
        Calls.Enqueue((contentType, audio.Length));
        return Task.FromResult(Encoding.ASCII.GetString(audio).Contains(Fail, StringComparison.Ordinal)
            ? TranscriptionOutcome.Failure(ProviderName, "The transcription provider did not respond within 60 seconds.")
            : TranscriptionOutcome.Success(ProviderName, Transcript));
    }
}
