using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PHANTOM.Application.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class DatabaseTests(PhantomApiFactory factory)
{
    [Fact]
    public async Task Migrations_are_fully_applied_and_roles_seeded()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        var roles = await db.Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RoleNames.All.Order(StringComparer.Ordinal), roles);
    }

    [Theory]
    [InlineData("UPDATE audit_logs SET action = 'tampered'")]
    [InlineData("DELETE FROM audit_logs")]
    [InlineData("TRUNCATE audit_logs")]
    public async Task Audit_log_rejects_modification_at_the_database(string sql)
    {
        await WriteAuditAsync("test.seed_row");
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_entry_commits_with_the_unit_of_work()
    {
        var action = $"test.committed_{Guid.NewGuid():N}";
        await WriteAuditAsync(action);
        Assert.Equal(1, await CountAsync(action));
    }

    [Fact]
    public async Task Audit_entry_is_discarded_when_the_transaction_rolls_back()
    {
        var action = $"test.rolled_back_{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                scope.ServiceProvider.GetRequiredService<IAuditWriter>().Record(action, "Test", "1");
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            });
        }

        Assert.Equal(0, await CountAsync(action));
    }

    private async Task WriteAuditAsync(string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IAuditWriter>().Record(action, "Test", "1", new { a = 1 }, new { a = 2 });
        await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().SaveChangesAsync();
    }

    private async Task<int> CountAsync(string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.CountAsync(a => a.Action == action);
    }
}
