using Kautuka.CredentialsHost.Database;

namespace Kautuka.CredentialsHost.Hosting;

internal sealed class ConnectorDraftCleanupService(ConnectorDraftRepository drafts, ILogger<ConnectorDraftCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { drafts.CleanupExpired(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConnectorDraftCleanupLog.Failed(logger, ex);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class ConnectorDraftCleanupLog
{
    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Connector draft expiry cleanup failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
