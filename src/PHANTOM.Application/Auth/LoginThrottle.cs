using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;

namespace PHANTOM.Application.Auth;

/// <summary>
/// Refuses sign-in for an account <em>from one client</em> after repeated failures (security steering: login is
/// throttled). Keyed by account and client IP, so an attacker who knows a staff email can lock out only their own
/// attempts, not the real user. Attempts refused while blocked are not counted, so a block cannot be kept alive by
/// hammering it. In-memory: fine for one API instance; a scaled-out deployment needs a shared store.
/// </summary>
public interface ILoginThrottle
{
    bool IsBlocked(Guid userId, string? client);
    void RecordFailure(Guid userId, string? client);
    void Reset(Guid userId, string? client);
}

internal sealed class LoginThrottle(IClock clock, IOptions<AuthOptions> options) : ILoginThrottle
{
    private readonly ConcurrentDictionary<(Guid, string), Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(Guid userId, string? client)
    {
        if (!_failures.TryGetValue(Key(userId, client), out var failures))
        {
            return false;
        }

        lock (failures)
        {
            Prune(failures);
            return failures.Count >= options.Value.MaxFailedLoginsPerAccount;
        }
    }

    public void RecordFailure(Guid userId, string? client)
    {
        var failures = _failures.GetOrAdd(Key(userId, client), _ => new Queue<DateTimeOffset>());
        lock (failures)
        {
            Prune(failures);
            failures.Enqueue(clock.UtcNow);
        }

        // Keep the table small: forget pairs whose failures have all expired.
        if (_failures.Count > 10_000)
        {
            foreach (var (key, queue) in _failures)
            {
                lock (queue)
                {
                    Prune(queue);
                    if (queue.Count == 0)
                    {
                        _failures.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    public void Reset(Guid userId, string? client) => _failures.TryRemove(Key(userId, client), out _);

    private void Prune(Queue<DateTimeOffset> failures)
    {
        var since = clock.UtcNow.AddMinutes(-options.Value.FailedLoginWindowMinutes);
        while (failures.Count > 0 && failures.Peek() < since)
        {
            failures.Dequeue();
        }
    }

    private static (Guid, string) Key(Guid userId, string? client) => (userId, string.IsNullOrWhiteSpace(client) ? "unknown" : client);
}
