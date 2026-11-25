namespace Kautuka.Manager.Hosting;

internal sealed class GatewayConnectRenewalService(
    DateTimeOffset renewAfterUtc,
    IHostApplicationLifetime lifetime,
    ILogger<GatewayConnectRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan remaining;
        while ((remaining = renewAfterUtc - DateTimeOffset.UtcNow) > TimeSpan.Zero)
            await Task.Delay(remaining < TimeSpan.FromDays(1) ? remaining : TimeSpan.FromDays(1), stoppingToken)
                .ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested) return;
        logger.LogInformation("Restarting Manager to renew the Gateway CONNECT certificates.");
        lifetime.StopApplication();
    }
}
