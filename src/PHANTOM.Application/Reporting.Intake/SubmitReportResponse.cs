namespace PHANTOM.Application.Reporting.Intake;

/// <summary>Returned to the public caller on successful submission.</summary>
public sealed record SubmitReportResponse(
    string Reference,
    DateTimeOffset SubmittedAt);
