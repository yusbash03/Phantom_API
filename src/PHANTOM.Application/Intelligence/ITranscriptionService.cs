using System.Text.RegularExpressions;

namespace PHANTOM.Application.Intelligence;

/// <summary>Speech-to-text for report voice recordings (Requirement 6.11). Implementations never throw.</summary>
public interface ITranscriptionService
{
    /// <summary>False when no provider is configured; recordings are then kept untranscribed.</summary>
    bool IsConfigured { get; }

    Task<TranscriptionOutcome> TranscribeAsync(string contentType, byte[] audio, CancellationToken cancellationToken);
}

public sealed record TranscriptionOutcome(string Provider, string? Text, string? FailureMessage)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(Text);

    public static TranscriptionOutcome Success(string provider, string text) => new(provider, text, null);

    public static TranscriptionOutcome Failure(string provider, string message) => new(provider, null, message);
}

/// <summary>Used when transcription is not configured.</summary>
public sealed class UnconfiguredTranscriptionService : ITranscriptionService
{
    public bool IsConfigured => false;

    public Task<TranscriptionOutcome> TranscribeAsync(string contentType, byte[] audio, CancellationToken cancellationToken) =>
        Task.FromResult(TranscriptionOutcome.Failure("none", "Transcription is not configured."));
}

/// <summary>
/// A spoken report may include the caller's phone number or email. Contact details never go to the AI
/// (Requirement 6.8), so they are removed from the transcript before it is added to the prompt. Staff still
/// see the stored transcript unchanged.
/// </summary>
public static partial class TranscriptRedaction
{
    public const string PhoneMarker = "[phone number removed]";
    public const string EmailMarker = "[email removed]";

    public static string Redact(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var withoutEmails = Email().Replace(transcript, EmailMarker);
        return Phone().Replace(withoutEmails, PhoneMarker);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Seven or more digits, allowing spaces, dots, dashes, brackets and a leading +, as phone numbers are spoken or written.
    [GeneratedRegex(@"\+?\(?\d(?:[\s().-]*\d){6,}", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();
}
