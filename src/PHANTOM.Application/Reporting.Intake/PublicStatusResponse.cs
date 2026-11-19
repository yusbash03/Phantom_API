namespace PHANTOM.Application.Reporting.Intake;

/// <summary>Coarse status returned to anonymous callers — no internal data.</summary>
public sealed record PublicStatusResponse(string Reference, PublicReportStatus Status);

/// <summary>The only states a public reporter can see (Requirement 4.10).</summary>
public enum PublicReportStatus
{
    Received,
    UnderReview,
    BeingHandled,
    Closed,
}
