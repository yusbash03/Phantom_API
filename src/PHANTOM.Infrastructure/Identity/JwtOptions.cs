using System.ComponentModel.DataAnnotations;

namespace PHANTOM.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "phantom-api";

    [Required]
    public string Audience { get; set; } = "phantom-ui";

    /// <summary>At least 32 bytes (256 bits). Supplied by environment/user-secrets only.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenLifetimeMinutes { get; set; } = 15;
}
