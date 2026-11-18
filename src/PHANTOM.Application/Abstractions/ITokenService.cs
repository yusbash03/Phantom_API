using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Abstractions;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed record GeneratedRefreshToken(string RawToken, string Hash);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user, IReadOnlyCollection<string> roles);
    GeneratedRefreshToken GenerateRefreshToken();
    string HashRefreshToken(string rawToken);
}
