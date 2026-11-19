namespace PHANTOM.Application.Abstractions;

/// <summary>Re-encodes images to strip embedded metadata (EXIF, GPS).</summary>
public interface IImageProcessor
{
    /// <summary>
    /// Decodes <paramref name="source"/>, applies its orientation, and re-encodes the pixels only, so no
    /// embedded metadata survives. Returns a new stream owned by the caller, or <c>null</c> when the
    /// content cannot be decoded as an image of the given type or exceeds the pixel limit.
    /// </summary>
    Task<MemoryStream?> StripMetadataAsync(Stream source, string contentType, CancellationToken cancellationToken = default);
}
