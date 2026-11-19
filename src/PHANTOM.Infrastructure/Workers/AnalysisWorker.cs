using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Intelligence;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Intelligence;
using PHANTOM.Infrastructure.Persistence;

namespace PHANTOM.Infrastructure.Workers;

/// <summary>In-process wake-up for the worker. Coalesces bursts; polling covers anything missed.</summary>
internal sealed class AnalysisSignal : IAnalysisSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.WaitToReadAsync(cts.Token);
            _channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }
}

/// <summary>
/// Processes the AI analysis queue held in the database (design §5), so work survives restarts.
/// Claims one Pending row at a time with <c>FOR UPDATE SKIP LOCKED</c>, which is safe with several
/// API instances, and returns rows stuck in Processing (e.g. after a crash) to Pending.
/// </summary>
internal sealed class AnalysisWorker(
    IServiceScopeFactory scopes,
    AnalysisSignal signal,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Analysis worker started (poll every {PollSeconds}s)", poll.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStuckAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested && await TryProcessNextAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // e.g. database briefly unavailable; claimed rows are recovered on a later pass.
                logger.LogError(ex, "Analysis worker pass failed");
            }

            await signal.WaitAsync(poll, stoppingToken);
        }
    }

    private async Task RecoverStuckAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(options.Value.StuckAfterMinutes);
        var recovered = await db.IncidentAnalyses
            .Where(a => a.Status == AnalysisStatus.Processing && a.ClaimedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AnalysisStatus.Pending).SetProperty(a => a.ClaimedAt, (DateTimeOffset?)null), cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning("Returned {Count} stuck analyses to the queue", recovered);
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        Guid analysisId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var now = time.GetUtcNow();
            // One statement: select and mark atomically, skipping rows another instance holds.
            var claimed = await db.Database.SqlQuery<Guid>(
                $"""
                UPDATE incident_analyses SET status = 'Processing', claimed_at = {now}
                WHERE id = (
                    SELECT id FROM incident_analyses
                    WHERE status = 'Pending'
                    ORDER BY requested_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (claimed.Count == 0)
            {
                return false;
            }

            analysisId = claimed[0];
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAnalysisService>().ProcessAsync(analysisId, cancellationToken);
        }

        return true;
    }
}
