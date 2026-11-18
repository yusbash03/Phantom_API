namespace PHANTOM.Domain.Identity;

public static class RoleNames
{
    public const string Administrator = "Administrator";
    public const string IncidentCoordinator = "IncidentCoordinator";
    public const string Responder = "Responder";

    public static readonly IReadOnlyList<string> All = [Administrator, IncidentCoordinator, Responder];

    public static bool IsValid(string role) => All.Contains(role, StringComparer.Ordinal);
}
