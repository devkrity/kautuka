using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Hosting;

internal sealed partial class NetworkAuditWorker(ContainerEgressRuntime runtime, ILogger<NetworkAuditWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try { await runtime.CollectNetworkAuditAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { LogCollectionFailed(logger, ex); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 7400, Level = LogLevel.Warning, Message = "Network audit collection failed; coverage may be incomplete.")]
    private static partial void LogCollectionFailed(ILogger logger, Exception exception);
}
