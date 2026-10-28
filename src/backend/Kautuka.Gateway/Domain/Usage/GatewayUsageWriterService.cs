namespace Kautuka.Gateway.Domain.Usage;

internal sealed partial class GatewayUsageWriterService(
    IGatewayUsageQueue queue,
    IGatewayUsageStore store,
    ILogger<GatewayUsageWriterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (GatewayUsageFact first in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    store.AppendBatch([first]);
                    queue.Complete(first);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    queue.RecordFailure(first, ex.GetType().Name);
                    LogUsageBatchPersistFailed(logger, ex, 1);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to persist usage fact batch of size {BatchSize}")]
    private static partial void LogUsageBatchPersistFailed(ILogger logger, Exception exception, int batchSize);
}
