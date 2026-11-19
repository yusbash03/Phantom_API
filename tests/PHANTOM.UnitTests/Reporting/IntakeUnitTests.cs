using PHANTOM.Application.Reporting.Intake;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Storage;

namespace PHANTOM.UnitTests.Reporting;

public sealed class MagicByteFileInspectorTests
{
    private readonly MagicByteFileInspector _inspector = new();

    public static TheoryData<byte[], string> Allowed => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10], "image/jpeg" },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8], "image/webp" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8], "audio/wav" },
        { [.. "OggS"u8, 0, 2], "audio/ogg" },
        { [.. "ID3"u8, 4, 0], "audio/mpeg" },
        { [0xFF, 0xFB, 0x90, 0x00], "audio/mpeg" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0x01], "audio/webm" },
        { [0, 0, 0, 0x20, .. "ftypM4A "u8], "audio/mp4" },
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
