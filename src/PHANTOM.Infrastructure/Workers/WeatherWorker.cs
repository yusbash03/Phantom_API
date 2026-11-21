using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Weather;
using PHANTOM.Infrastructure.Weather;

namespace PHANTOM.Infrastructure.Workers;

/// <summary>
/// Retrieves weather for active incidents once they have coordinates, and again when those coordinates move
/// (Requirement 9.1). A failed retrieval is retried after a pause. Never touches the incident itself.
/// </summary>
internal sealed partial class WeatherWorker(IServiceScopeFactory scopes, IOptions<WeatherOptions> options, ILogger<WeatherWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        var retryAfter = TimeSpan.FromMinutes(options.Value.RetryFailedAfterMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<IEnvironmentService>().RetrieveDueAsync(BatchSize, retryAfter, stoppingToken);
                }
                while (processed == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPassFailed(logger, ex);
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Weather worker pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
