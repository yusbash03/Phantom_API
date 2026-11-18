using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PHANTOM.Infrastructure.Identity;

public static class JwtSigningKeys
{
    public static SymmetricSecurityKey Create(string key) => new(Encoding.UTF8.GetBytes(key ?? string.Empty));
}
