using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;

namespace PHANTOM.Infrastructure.Persistence;

/// <summary>A transaction-scoped PostgreSQL advisory lock keyed by name; released automatically on commit or rollback.</summary>
internal sealed class PostgresExclusiveSection(PhantomDbContext db) : IExclusiveSection
{
    public Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        // The retrying execution strategy requires user transactions to run inside it as one retriable unit.
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({name}, 0))", cancellationToken);
            var result = await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
