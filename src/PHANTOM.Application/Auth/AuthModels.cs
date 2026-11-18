namespace PHANTOM.Application.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>Result of a successful login or refresh. The raw refresh token goes only into an HttpOnly cookie.</summary>
public sealed record AuthSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    CurrentUserResponse User);
