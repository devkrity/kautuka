namespace Kautuka.Gateway.Domain.Assets;

internal sealed partial class ConversationAssetRecoveryService(
    ConversationAssetStore assetStore,
    ILogger<ConversationAssetRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await assetStore.ReconcileStagedUploadsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogReconciliationFailed(logger, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Interrupted conversation asset upload reconciliation failed")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception);
}
