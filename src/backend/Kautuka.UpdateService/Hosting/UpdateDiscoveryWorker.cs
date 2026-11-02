using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class UpdateDiscoveryWorker(
    IUpdateCoordinator updateCoordinator,
    IOptions<UpdateOptions> options,
    ILogger<UpdateDiscoveryWorker> logger) : BackgroundService
{
    private readonly IUpdateCoordinator updateCoordinator = updateCoordinator;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<UpdateDiscoveryWorker> logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Manager update discovery is disabled.");
            return;
        }

        using PeriodicTimer timer = new(options.DiscoveryInterval);

        try
        {
            // The updater may restart before Manager has finished coming up.
            // Reconcile pending activation independently of UI polling rather
            // than waiting for the next daily discovery interval.
            await ReconcilePendingActivationAsync(updateCoordinator, options.HostServiceActivationTimeout, TimeSpan.FromSeconds(5), stoppingToken, logger).ConfigureAwait(false);
            await updateCoordinator.RefreshDiscoveryAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Manager update discovery startup attempt failed.");
        }

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await updateCoordinator.RefreshDiscoveryAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manager update discovery attempt failed.");
            }
        }
    }

    internal static async Task ReconcilePendingActivationAsync(
        IUpdateCoordinator coordinator, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                UpdateStatusSnapshot status = await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status.State != UpdateWorkflowState.Applying)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Update activation observation failed; retrying while the runtime starts.");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
