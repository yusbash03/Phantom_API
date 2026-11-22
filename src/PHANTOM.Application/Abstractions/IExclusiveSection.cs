namespace PHANTOM.Application.Abstractions;

/// <summary>
/// Runs work in a database transaction while holding a named, cluster-wide lock, so check-then-write rules that
/// span several rows (e.g. "never remove the last active administrator") cannot be raced by two requests.
/// </summary>
public interface IExclusiveSection
{
    Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
