namespace PHANTOM.Application.Abstractions;

/// <summary>
/// One-way, keyed pseudonym for a client IP, kept for abuse analysis only. Keyed (HMAC) so the small IPv4 space
/// cannot be brute-forced back to addresses by anyone holding the database without the key.
/// </summary>
public interface IClientIpHasher
{
    string? Hash(string? ip);
}
