using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Keeps container-runtime availability warm so GetHostStatus and reconcile
/// read a last-known sample instead of inducing <c>podman info</c>.
/// </summary>
public sealed class ManagerRuntimeHealthWarmWorker(
    IManagerRuntimeHealthProbe healthProbe,
    ILogger<ManagerRuntimeHealthWarmWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan HealthyRefreshInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UnavailableRefreshInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool available;
            try
            {
                available = await healthProbe.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Container runtime health refresh failed; treating runtime as unavailable.");
                available = false;
            }

            TimeSpan delay = available ? HealthyRefreshInterval : UnavailableRefreshInterval;
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
