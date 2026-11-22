using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Identity;

/// <summary>
/// HMAC-SHA256 of the client IP. The key is <c>Privacy:IpHashKey</c> when set, otherwise derived (HKDF) from the
/// JWT signing key, which is already a required secret; rotating either changes future hashes only.
/// </summary>
internal sealed class HmacClientIpHasher : IClientIpHasher
{
    private readonly byte[] _key;

    public HmacClientIpHasher(IOptions<JwtOptions> jwt, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var configured = configuration["Privacy:IpHashKey"];
        _key = !string.IsNullOrWhiteSpace(configured)
            ? Encoding.UTF8.GetBytes(configured)
            : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt.Value.SigningKey), 32, info: "phantom-client-ip-hash-v1"u8.ToArray());
    }

    public string? Hash(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(ip.Trim())));
}
