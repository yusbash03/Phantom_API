namespace PHANTOM.Infrastructure.Identity;

/// <summary>Short JWT claim names. Inbound claim mapping is disabled so these arrive unchanged.</summary>
public static class PhantomClaimTypes
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
}
