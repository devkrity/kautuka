using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

internal sealed partial class ChannelQueueCardExpiryWorker(
    ChannelSessionFactsOutboundExecutor executor,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<ChannelQueueCardExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await executor.ExpireQueueCardsAsync(adapters, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
            {
                ExpiryFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Queue card expiry failed; it will retry on the next interval.")]
    private static partial void ExpiryFailed(ILogger logger, Exception exception);
}
