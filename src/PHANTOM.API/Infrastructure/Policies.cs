using PHANTOM.Domain.Identity;

namespace PHANTOM.API.Infrastructure;

public static class Policies
{
    public const string Administrator = "RequireAdministrator";
    public const string Coordinator = "RequireCoordinator";
    public const string Responder = "RequireResponder";

    /// <summary>Administrator or Incident Coordinator.</summary>
    public const string Staff = "RequireOperationalStaff";

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Every endpoint requires authentication unless it explicitly opts out with [AllowAnonymous].
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        options.AddPolicy(Administrator, p => p.RequireRole(RoleNames.Administrator));
        options.AddPolicy(Coordinator, p => p.RequireRole(RoleNames.IncidentCoordinator));
        options.AddPolicy(Responder, p => p.RequireRole(RoleNames.Responder));
        options.AddPolicy(Staff, p => p.RequireRole(RoleNames.Administrator, RoleNames.IncidentCoordinator));
    }
}
