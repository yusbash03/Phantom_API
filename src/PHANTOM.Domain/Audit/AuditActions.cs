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
    public const string AiAnalysisQueued = "ai_analysis.queued";
}
