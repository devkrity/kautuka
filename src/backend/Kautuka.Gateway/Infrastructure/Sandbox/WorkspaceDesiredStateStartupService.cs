namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed class WorkspaceDesiredStateStartupService(
    IWorkspaceDesiredStatePublisher publisher,
    ILogger<WorkspaceDesiredStateStartupService> logger) : BackgroundService
{
    private static readonly TimeSpan publishRetryInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkspaceDesiredStateStartupServiceLog.PublishAttemptStarting(logger, "gateway-startup");
        bool published = (await PublishDesiredStateAsync("gateway-startup", stoppingToken).ConfigureAwait(false)).Accepted;
        WorkspaceDesiredStateStartupServiceLog.PublishAttemptCompleted(logger, "gateway-startup", published);

        while (!published && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(publishRetryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            const string reason = "gateway-startup-retry";
            WorkspaceDesiredStateStartupServiceLog.PublishAttemptStarting(logger, reason);
            published = (await PublishDesiredStateAsync(reason, stoppingToken).ConfigureAwait(false)).Accepted;
            WorkspaceDesiredStateStartupServiceLog.PublishAttemptCompleted(logger, reason, published);
        }
    }

    private Task<DesiredStatePublishResult> PublishDesiredStateAsync(string reason, CancellationToken cancellationToken)
    {

        using IDisposable scope = logger.BeginKautukaCorrelationScope(("PublishReason", reason));
        return publisher.TryPublishCurrentAsync(reason, cancellationToken);
    }
}

internal static partial class WorkspaceDesiredStateStartupServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "[GatewayStartup] Desired-state publish attempt starting. reason={Reason}")]
    public static partial void PublishAttemptStarting(ILogger logger, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "[GatewayStartup] Desired-state publish attempt completed. reason={Reason} success={Success}")]
    public static partial void PublishAttemptCompleted(ILogger logger, string reason, bool success);
}
