using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure.Persistence;

namespace PHANTOM.Infrastructure.Persistence;

/// <summary>Generates incident numbers using the <c>incident_number_seq</c> PostgreSQL sequence.</summary>
internal sealed class PostgresIncidentNumberGenerator(PhantomDbContext db, IClock clock) : IIncidentNumberGenerator
{
    public async Task<string> NextAsync(CancellationToken cancellationToken = default)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nextval('incident_number_seq')";

            if (db.Database.CurrentTransaction is { } tx)
            {
                cmd.Transaction = tx.GetDbTransaction();
            }

            var seqValue = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            var year = clock.UtcNow.Year;
            return $"PH-{year}-{seqValue:D6}";
        }
        finally
        {
            if (!wasOpen)
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
