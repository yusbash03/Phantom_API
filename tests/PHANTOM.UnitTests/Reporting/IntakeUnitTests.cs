using PHANTOM.Application.Reporting.Intake;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Storage;

namespace PHANTOM.UnitTests.Reporting;

public sealed class MagicByteFileInspectorTests
{
    private readonly MagicByteFileInspector _inspector = new();

    /// <summary>MPEG-1 Layer III, 128 kbit/s, 44.1 kHz: a 417-byte frame.</summary>
    internal static byte[] Mp3Frames(int count = 2)
    {
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        return [.. Enumerable.Repeat(frame, count).SelectMany(f => f)];
    }

    /// <summary>A minimal EBML header declaring DocType "webm" (as browsers record it).</summary>
    internal static byte[] WebMHeader(string docType = "webm") =>
        [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81, 0x04, 0x42, 0xF3, 0x81, 0x08,
         0x42, 0x82, (byte)(0x80 | docType.Length), .. System.Text.Encoding.ASCII.GetBytes(docType), 0x42, 0x87, 0x81, 0x04];

    public static TheoryData<byte[], string> Allowed => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10], "image/jpeg" },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8], "image/webp" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8], "audio/wav" },
        { [.. "OggS"u8, 0, 2], "audio/ogg" },
        { Mp3Frames(), "audio/mpeg" },
        { [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, .. Mp3Frames()], "audio/mpeg" },
        { WebMHeader(), "audio/webm" },
        { [0, 0, 0, 0x20, .. "ftypM4A "u8], "audio/mp4" },
        { [0, 0, 0, 0x20, .. "ftypiso5"u8], "audio/mp4" },
    };

    public static TheoryData<byte[]> Rejected => new()
    {
        "%PDF-1.7"u8.ToArray(),
        "MZ\u0090\0\u0003\0"u8.ToArray(),
        "<svg xmlns"u8.ToArray(),
        "GIF89a\u0001\0"u8.ToArray(),
        (byte[])[.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8],
        (byte[])[0x50, 0x4B, 0x03, 0x04, 0x14, 0x00],
        (byte[])[0xFF, 0xD8],
        (byte[])[],
        // An audio signature followed by something else is not audio.
        (byte[])[.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, .. "<html><script>alert(1)</script></html>"u8],
        (byte[])[0xFF, 0xFB, 0x90, 0x00, .. "MZ this is an executable, not a second frame"u8],
        Mp3Frames(count: 1),
        (byte[])[.. "ID3"u8, 9, 0, 0, 0, 0, 0, 0, .. Mp3Frames()],
        WebMHeader("matroska"),
        (byte[])[0x1A, 0x45, 0xDF, 0xA3, 0x01],
        (byte[])[0, 0, 0, 0x18, .. "ftypheic"u8],
        (byte[])[0, 0, 0, 0x18, .. "ftypavif"u8],
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Detects_allowed_types_from_content(byte[] header, string expected) =>
        Assert.Equal(expected, _inspector.DetectAllowedMimeType(header));

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Rejects_everything_else(byte[] header) =>
        Assert.Null(_inspector.DetectAllowedMimeType(header));
}

public sealed class PublicStatusMappingTests
{
    [Fact]
    public void Every_incident_status_maps_to_a_coarse_public_status()
    {
        var expected = new Dictionary<IncidentStatus, PublicReportStatus>
        {
            [IncidentStatus.Reported] = PublicReportStatus.Received,
            [IncidentStatus.UnderReview] = PublicReportStatus.UnderReview,
            [IncidentStatus.Verified] = PublicReportStatus.UnderReview,
            [IncidentStatus.Assigned] = PublicReportStatus.BeingHandled,
            [IncidentStatus.InProgress] = PublicReportStatus.BeingHandled,
            [IncidentStatus.Resolved] = PublicReportStatus.Closed,
            [IncidentStatus.Closed] = PublicReportStatus.Closed,
            [IncidentStatus.Rejected] = PublicReportStatus.Closed,
        };

        Assert.Equal(Enum.GetValues<IncidentStatus>().Order(), expected.Keys.Order());
        foreach (var (status, publicStatus) in expected)
        {
            Assert.Equal(publicStatus, ReportIntakeService.ToPublicStatus(status));
        }
    }
}
