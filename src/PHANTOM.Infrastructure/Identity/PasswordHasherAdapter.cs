using Microsoft.AspNetCore.Identity;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Infrastructure.Identity;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (versioned format, automatic rehash on parameter upgrades).</summary>
internal sealed class PasswordHasherAdapter : IPasswordHasher
{
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(null!, "phantom-timing-equaliser"));

    public string Hash(User user, string password) => Hasher.HashPassword(user, password);

    public PasswordCheck Verify(User user, string hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            SimulateVerification(password);
            return PasswordCheck.Failed;
        }

        return Hasher.VerifyHashedPassword(user, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
    }

    public void SimulateVerification(string password) =>
        _ = Hasher.VerifyHashedPassword(null!, DummyHash.Value, password);
}
