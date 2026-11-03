using Kautuka.UpdateService.Abstractions;
using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class HostedCatalogRefreshWorker(
    IHostedCatalogService hostedCatalogService,
    IOptions<UpdateOptions> options,
    ILogger<HostedCatalogRefreshWorker> logger) : BackgroundService
{
    private readonly IHostedCatalogService hostedCatalogService = hostedCatalogService;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<HostedCatalogRefreshWorker> logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Hosted catalog refresh is disabled because updates are disabled.");
            return;
        }

        using PeriodicTimer timer = new(options.CatalogRefreshInterval);

        try
        {
            if (KautukaMaintenanceMode.IsActive)
            {
                logger.LogInformation("Hosted catalog refresh is paused while safe update recovery is fenced.");
                return;
            }

            await hostedCatalogService.RefreshSnapshotAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hosted catalog refresh startup attempt failed.");
        }

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (KautukaMaintenanceMode.IsActive)
            {
                continue;
            }

            try
            {
                await hostedCatalogService.RefreshSnapshotAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hosted catalog refresh attempt failed.");
            }
        }
    }
}
