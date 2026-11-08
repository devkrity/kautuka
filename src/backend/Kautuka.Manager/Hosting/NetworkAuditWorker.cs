using System.Diagnostics;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Hosting;

internal sealed partial class NetworkAuditWorker(ContainerEgressRuntime runtime, ILogger<NetworkAuditWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        long nextDnsCheck = 0;
        do
        {
            try { await runtime.CollectNetworkAuditAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { LogCollectionFailed(logger, ex); }
            if (Stopwatch.GetTimestamp() >= nextDnsCheck)
            {
                try { await runtime.RefreshHostDnsAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { LogDnsRefreshFailed(logger, ex); }
                nextDnsCheck = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 7400, Level = LogLevel.Warning, Message = "Network audit collection failed; coverage may be incomplete.")]
    private static partial void LogCollectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning, Message = "Host DNS refresh failed; the previous confirmed configuration or recovery state is retained.")]
    private static partial void LogDnsRefreshFailed(ILogger logger, Exception exception);
}
