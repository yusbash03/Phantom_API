namespace PHANTOM.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>Failed logins allowed per account within <see cref="FailedLoginWindowMinutes"/> before further attempts are refused.</summary>
    public int MaxFailedLoginsPerAccount { get; set; } = 10;

    public int FailedLoginWindowMinutes { get; set; } = 15;
}
