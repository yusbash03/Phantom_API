namespace PHANTOM.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>A signed-in session cannot be kept alive by refreshing beyond this; the user signs in again.</summary>
    public int AbsoluteSessionLifetimeDays { get; set; } = 30;

    /// <summary>
    /// Failed logins allowed per account <em>from one client</em> within <see cref="FailedLoginWindowMinutes"/> before
    /// that client's further attempts are refused (see <see cref="ILoginThrottle"/>).
    /// </summary>
    public int MaxFailedLoginsPerAccount { get; set; } = 10;

    public int FailedLoginWindowMinutes { get; set; } = 15;
}
