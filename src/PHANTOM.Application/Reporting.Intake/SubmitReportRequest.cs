using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Reporting.Intake;

/// <summary>Parsed and validated data from a public report submission.</summary>
public sealed class SubmitReportRequest
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IncidentCategory Category { get; init; }
    public required string LocationText { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public string? ReporterName { get; init; }
    public string? ReporterPhone { get; init; }
    public string? ReporterEmail { get; init; }
    public string? IdempotencyKey { get; init; }

    /// <summary>Client IP address, used only for salted hash abuse analysis. Never stored raw.</summary>
    public string? ClientIp { get; init; }

    /// <summary>Each entry: (original file name, file content stream, client-supplied content type).</summary>
    public IReadOnlyList<FileAttachment> Files { get; init; } = [];
}

public sealed class FileAttachment(string originalFileName, Stream stream, string clientContentType)
{
    public string OriginalFileName { get; } = originalFileName;
    public Stream Stream { get; } = stream;
    public string ClientContentType { get; } = clientContentType;
}
