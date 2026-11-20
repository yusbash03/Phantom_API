namespace PHANTOM.Domain.Audit;

/// <summary>Stable audit action names. Grouped by module; values are persisted, never rename.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string RefreshTokenReuseDetected = "auth.refresh_token_reuse_detected";

    public const string UserCreated = "user.created";
    public const string UserRolesChanged = "user.roles_changed";
    public const string UserDeactivated = "user.deactivated";
    public const string UserActivated = "user.activated";

    public const string ReportSubmitted = "report.submitted";
    public const string IncidentCreated = "incident.created";
    public const string IncidentUpdated = "incident.updated";
    public const string IncidentStatusChanged = "incident.status_changed";
    public const string IncidentSeverityChanged = "incident.severity_changed";
    public const string LocationGeocoded = "incident.location_geocoded";
    public const string LocationGeocodingFailed = "incident.location_geocoding_failed";
    public const string LocationVerified = "incident.location_verified";
    public const string AiAnalysisQueued = "ai_analysis.queued";
    public const string AiAnalysisCompleted = "ai_analysis.completed";
    public const string AiAnalysisFailed = "ai_analysis.failed";
    public const string AiAnalysisRetryRequested = "ai_analysis.retry_requested";

    public const string ResponderCreated = "responder.created";
    public const string ResponderUpdated = "responder.updated";
    public const string ResponderDeactivated = "responder.deactivated";
    public const string ResponderActivated = "responder.activated";
    public const string ResponderAvailabilityChanged = "responder.availability_changed";

    public const string AssignmentCreated = "assignment.created";
    public const string AssignmentStatusChanged = "assignment.status_changed";
    public const string AssignmentUpdatePosted = "assignment.update_posted";

    public const string RelationSuggested = "relation.suggested";
    public const string RelationLinked = "relation.linked";
    public const string RelationDecided = "relation.decided";
    public const string ReportReassociated = "report.reassociated";
}
