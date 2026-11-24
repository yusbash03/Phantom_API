using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Storage;

/// <summary>
/// Detects file type from content (magic bytes and minimal structure), ignoring client-supplied names and types.
/// Images are re-encoded later, so a signature is enough for them. Audio is stored as uploaded, so it must also
/// look like real audio structure: a short signature alone would let arbitrary bytes through as a "recording".
/// </summary>
internal sealed class MagicByteFileInspector : IFileInspector
{
    // ISO-BMFF brands that are images, not audio (HEIC/HEIF/AVIF); everything else in an ftyp box is treated as M4A.
    private static readonly string[] ImageBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1", "avif", "avis"];

    public string? DetectAllowedMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        if (StartsWith(data, [0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (StartsWith(data, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        // RIFF containers: only WEBP and WAVE are allowed.
        if (StartsWith(data, "RIFF"u8))
        {
            return data.Length < 12 ? null
                : data.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
                : data.Slice(8, 4).SequenceEqual("WAVE"u8) ? "audio/wav"
                : null;
        }

        if (StartsWith(data, "OggS"u8))
        {
            return "audio/ogg";
        }

        // WebM: an EBML header whose DocType is "webm" (Matroska video files say "matroska").
        if (StartsWith(data, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return IsWebM(data) ? "audio/webm" : null;
        }

        // MP4/M4A: "ftyp" box at offset 4 with a brand that is not an image format.
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(data.Slice(8, 4)).ToLowerInvariant();
            return ImageBrands.Contains(brand, StringComparer.Ordinal) ? null : "audio/mp4";
        }

        // MP3: an optional, well-formed ID3v2 tag, then two consecutive valid MPEG audio frames.
        return IsMp3(data) ? "audio/mpeg" : null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static bool IsWebM(ReadOnlySpan<byte> data)
    {
        // DocType element (0x42 0x82) appears early in the EBML header; its value is a short ASCII string.
        var header = data[..Math.Min(data.Length, 64)];
        for (var i = 4; i + 2 < header.Length; i++)
        {
            if (header[i] == 0x42 && header[i + 1] == 0x82)
            {
                var size = header[i + 2] & 0x7F;
                return i + 3 + size <= header.Length && header.Slice(i + 3, size).SequenceEqual("webm"u8);
            }
        }

        return false;
    }

    private static bool IsMp3(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        if (StartsWith(data, "ID3"u8))
        {
            // ID3v2: "ID3", major version 2-4, revision, flags, then a 4-byte synchsafe size (7 bits per byte).
            if (data.Length < 10 || data[3] is < 2 or > 4 || data[6] >= 0x80 || data[7] >= 0x80 || data[8] >= 0x80 || data[9] >= 0x80)
            {
                return false;
            }

            offset = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9]);
        }

        var first = FrameLength(data, offset);
        return first > 0 && FrameLength(data, offset + first) > 0;
    }

    private static readonly int[] Mpeg1Layer3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer3Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>The length of a valid MPEG-1/2/2.5 Layer III frame starting at <paramref name="offset"/>, or 0.</summary>
    internal static int FrameLength(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length || data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
        {
            return 0;
        }

        var version = (data[offset + 1] >> 3) & 0x03;    // 00 = 2.5, 01 = reserved, 10 = 2, 11 = 1
        var layer = (data[offset + 1] >> 1) & 0x03;      // 01 = Layer III
        var bitrateIndex = (data[offset + 2] >> 4) & 0x0F;
        var rateIndex = (data[offset + 2] >> 2) & 0x03;
        var padding = (data[offset + 2] >> 1) & 0x01;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return 0;
        }

        int[] rates = version switch { 3 => [44100, 48000, 32000], 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] };
        var bitrate = (version == 3 ? Mpeg1Layer3Bitrates : Mpeg2Layer3Bitrates)[bitrateIndex] * 1000;
        var samplesFactor = version == 3 ? 144 : 72;
        return (samplesFactor * bitrate / rates[rateIndex]) + padding;
    }
}
