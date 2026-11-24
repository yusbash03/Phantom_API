using Microsoft.Extensions.Logging;
using PHANTOM.Application.Abstractions;
using SkiaSharp;

namespace PHANTOM.Infrastructure.Storage;

/// <summary>
/// Re-encodes images with SkiaSharp. Only pixels are written back, so EXIF (including GPS), XMP and
/// ICC metadata are dropped. EXIF orientation is applied to the pixels first so photos stay upright.
/// </summary>
internal sealed class SkiaSharpImageProcessor(ILogger<SkiaSharpImageProcessor> logger) : IImageProcessor
{
    /// <summary>Guards against decompression bombs: a small file declaring enormous dimensions (24 MP ≈ 96 MB decoded).</summary>
    public const long MaxPixels = 24_000_000;

    /// <summary>At most two images are decoded at once, bounding memory under parallel anonymous uploads.</summary>
    private static readonly SemaphoreSlim Decoders = new(2, 2);

    private const int Quality = 90;

    public async Task<MemoryStream?> StripMetadataAsync(Stream source, string contentType, CancellationToken cancellationToken = default)
    {
        await Decoders.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StripCore(source, contentType, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Decoders.Release();
        }
    }

    private Task<MemoryStream?> StripCore(Stream source, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        cancellationToken.ThrowIfCancellationRequested();

        var format = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/png" => SKEncodedImageFormat.Png,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentException($"Unsupported image type '{contentType}'.", nameof(contentType)),
        };

        using var codec = SKCodec.Create(source);
        if (codec is null || codec.EncodedFormat != format)
        {
            return Task.FromResult<MemoryStream?>(null);
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
        {
            logger.LogInformation("Rejected image with dimensions {Width}x{Height}", info.Width, info.Height);
            return Task.FromResult<MemoryStream?>(null);
        }

        using var decoded = new SKBitmap(info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul));
        var decodeResult = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (decodeResult is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return Task.FromResult<MemoryStream?>(null);
        }

        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(oriented ?? decoded);
        using var encoded = image.Encode(format, Quality);
        if (encoded is null)
        {
            return Task.FromResult<MemoryStream?>(null);
        }

        var output = new MemoryStream();
        encoded.SaveTo(output);
        output.Position = 0;
        return Task.FromResult<MemoryStream?>(output);
    }

    /// <summary>Returns a new bitmap with the EXIF orientation baked in, or null when no change is needed.</summary>
    private static SKBitmap? ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return null;
        }

        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;

        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, height);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }
}
