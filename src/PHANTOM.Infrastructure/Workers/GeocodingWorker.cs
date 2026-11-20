using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Geocoding;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Geocoding;
using PHANTOM.Infrastructure.Persistence;

namespace PHANTOM.Infrastructure.Workers;

internal sealed class GeocodingSignal : IGeocodingSignal
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
/// Geocodes incidents whose state is Pending (Requirement 8.2). Same queue pattern as the analysis worker:
/// atomic claim with FOR UPDATE SKIP LOCKED, and recovery of claims stuck after a crash.
/// </summary>
internal sealed class GeocodingWorker(
    IServiceScopeFactory scopes,
    GeocodingSignal signal,
    IOptions<MapboxOptions> options,
    TimeProvider time,
    ILogger<GeocodingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Geocoding worker started (poll every {PollSeconds}s)", poll.TotalSeconds);
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
                logger.LogError(ex, "Geocoding worker pass failed");
            }

            await signal.WaitAsync(poll, stoppingToken);
        }
    }

    private async Task RecoverStuckAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(options.Value.StuckAfterMinutes);
        var recovered = await db.Incidents
            .Where(i => i.GeocodingStatus == GeocodingStatus.Processing && i.GeocodingClaimedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.GeocodingStatus, GeocodingStatus.Pending).SetProperty(i => i.GeocodingClaimedAt, (DateTimeOffset?)null), cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning("Returned {Count} stuck geocoding claims to the queue", recovered);
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        Guid incidentId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var now = time.GetUtcNow();
            var claimed = await db.Database.SqlQuery<Guid>(
                $"""
                UPDATE incidents SET geocoding_status = 'Processing', geocoding_claimed_at = {now}
                WHERE id = (
                    SELECT id FROM incidents
                    WHERE geocoding_status = 'Pending'
                    ORDER BY created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (claimed.Count == 0)
            {
                return false;
            }

            incidentId = claimed[0];
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGeocodingProcessor>().ProcessAsync(incidentId, cancellationToken);
        }

        return true;
    }
}
