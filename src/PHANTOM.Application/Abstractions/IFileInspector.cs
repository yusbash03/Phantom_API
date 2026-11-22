namespace PHANTOM.Application.Abstractions;

/// <summary>Detects the true content type of a file from its magic bytes, independent of the client-supplied name or Content-Type.</summary>
public interface IFileInspector
{
    /// <summary>
    /// Inspects <paramref name="data"/> (the whole file) and returns the detected MIME type, or <c>null</c> if the
    /// content cannot be identified as an allowed type.
    /// </summary>
    string? DetectAllowedMimeType(ReadOnlySpan<byte> data);
}
