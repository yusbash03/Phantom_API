using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Abstractions;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(User user, string password);
    PasswordCheck Verify(User user, string hash, string password);

    /// <summary>Performs equivalent work to a verification so unknown accounts take as long as known ones.</summary>
    void SimulateVerification(string password);
}
