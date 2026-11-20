using PHANTOM.Domain.Incidents;

namespace PHANTOM.Application.Incidents;

public enum IncidentSortField
{
    CreatedAt,
    UpdatedAt,
    Severity,
    Status,
}

public enum SortDirection
{
    Desc,
    Asc,
}

/// <summary>Coordinator operations that may be offered on an incident, given its status and the caller's role.</summary>
public enum IncidentAction
{
    Edit,
    BeginReview,
    Verify,
    Reject,
    Resolve,
    Reopen,
    Close,
    ChangeSeverity,
    VerifyLocation,
    Assign,
}

public sealed record IncidentListQuery(
    IReadOnlyList<IncidentStatus>? Status = null,
    IncidentCategory? Category = null,
    Severity? Severity = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    bool? HasActiveAssignment = null,
    IncidentSortField Sort = IncidentSortField.CreatedAt,
    SortDirection Direction = SortDirection.Desc,
    int Page = 1,
    int PageSize = 25);

public sealed record IncidentListItem(
    Guid Id,
    string Number,
    string Title,
    IncidentCategory Category,
    IncidentStatus Status,
    Severity? ApprovedSeverity,
    Severity? AiRecommendedSeverity,
    string LocationText,
    bool HasCoordinates,
    bool LocationIsVerified,
    bool LocationNeedsVerification,
    int ReportCount,
    int PossibleDuplicateCount,
    bool IsDemoData,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserRef(Guid Id, string DisplayName);

public sealed record LocationResponse(
    string Text,
    decimal? Latitude,
    decimal? Longitude,
    LocationProvenance Provenance,
    LocationPrecision Precision,
    bool IsVerified,
    UserRef? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    string? GeocoderPlaceName);

public sealed record AttachmentResponse(Guid Id, AttachmentKind Kind, string ContentType, long SizeBytes, string OriginalFileName, DateTimeOffset CreatedAt);

/// <summary>An original public submission. Immutable; contact details are visible to staff only.</summary>
public sealed record IncidentReportResponse(
    Guid Id,
    string Reference,
    bool IsOriginal,
    string Title,
    string Description,
    IncidentCategory Category,
    string LocationText,
    decimal? ReportedLatitude,
    decimal? ReportedLongitude,
    string? ReporterName,
    string? ReporterPhone,
    string? ReporterEmail,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<AttachmentResponse> Attachments);

public sealed record IncidentDetailResponse(
    Guid Id,
    string Number,
    string Title,
    string Description,
    IncidentCategory Category,
    IncidentCategory? AiRecommendedCategory,
    IncidentStatus Status,
    Severity? ApprovedSeverity,
    Severity? AiRecommendedSeverity,
    LocationResponse Location,
    bool LocationNeedsVerification,
    GeocodingStatus GeocodingStatus,
    UserRef? AssignedCoordinator,
    UserRef? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    string? ResolutionSummary,
    UserRef? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ClosureReason,
    DateTimeOffset? ClosedAt,
    bool IsDemoData,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    IReadOnlyList<IncidentAction> AvailableActions,
    IReadOnlyList<IncidentReportResponse> Reports);

/// <summary>Every write carries the <c>Version</c> the client last read; a stale version is a conflict.</summary>
public sealed record VersionedRequest(int Version);

public sealed record EditIncidentRequest(string Title, string Description, IncidentCategory? Category, int Version);

/// <summary>An omitted severity is a validation error, never a silent default.</summary>
public sealed record VerifyIncidentRequest(Severity? ApprovedSeverity, int Version);

public sealed record ReasonRequest(string Reason, int Version);

public sealed record ResolveIncidentRequest(string ResolutionSummary, int Version);

public sealed record ChangeSeverityRequest(Severity? Severity, int Version);

public sealed record AttachmentContent(Stream Content, string ContentType, string FileName);

/// <summary>A coordinator confirms or corrects the location; the result is the only verified kind (Requirement 8.5).</summary>
public sealed record VerifyLocationRequest(string Text, decimal? Latitude, decimal? Longitude, int Version);

/// <summary>Map filters (Requirement 8.7). The bounding box is optional; give all four bounds or none.</summary>
public sealed record IncidentMapQuery(
    IReadOnlyList<IncidentStatus>? Status = null,
    IncidentCategory? Category = null,
    Severity? Severity = null,
    decimal? MinLatitude = null,
    decimal? MinLongitude = null,
    decimal? MaxLatitude = null,
    decimal? MaxLongitude = null);

/// <summary>One plotted incident. Unverified locations are labelled as such wherever they appear (8.8).</summary>
public sealed record MapIncident(
    Guid Id,
    string Number,
    string Title,
    IncidentCategory Category,
    IncidentStatus Status,
    Severity? ApprovedSeverity,
    Severity? AiRecommendedSeverity,
    decimal Latitude,
    decimal Longitude,
    LocationProvenance Provenance,
    LocationPrecision Precision,
    bool IsVerified,
    string LocationText,
    string? GeocoderPlaceName,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>WithoutCoordinates</c> counts matching incidents that cannot be plotted, so the map never implies
/// it shows everything. <c>Truncated</c> is true when more than <see cref="MaxItems"/> matched.
/// </summary>
public sealed record IncidentMapResponse(IReadOnlyList<MapIncident> Items, int WithoutCoordinates, bool Truncated)
{
    public const int MaxItems = 1000;
}
