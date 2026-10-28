using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed class HitlExpirySweeperService(
    IGatewayApprovalStore approvalStore,
    IApprovalResolutionService approvalResolutionService,
    ILogger<HitlExpirySweeperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                HitlExpirySweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<GatewayToolApprovalRequest> due = approvalStore.ListDueForExpiry(now);
        foreach (GatewayToolApprovalRequest request in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await approvalResolutionService.ExpireAsync(request.Id, now, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static partial class HitlExpirySweeperLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "HITL expiry sweep failed.")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}
