using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Infrastructure.Identity;

internal sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(User user, IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);
        var settings = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(settings.AccessTokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
        };
        claims.AddRange(roles.Select(r => new Claim(PhantomClaimTypes.Role, r)));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(JwtSigningKeys.Create(settings.SigningKey), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expires);
    }

    public GeneratedRefreshToken GenerateRefreshToken()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return new GeneratedRefreshToken(raw, HashRefreshToken(raw));
    }

    public string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
