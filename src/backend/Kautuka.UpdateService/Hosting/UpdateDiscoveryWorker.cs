using Kautuka.UpdateService.Abstractions;

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
            _ = await updateCoordinator.GetStatusAsync(stoppingToken).ConfigureAwait(false);
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
}
