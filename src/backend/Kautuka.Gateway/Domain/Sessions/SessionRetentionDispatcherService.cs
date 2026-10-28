using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Sessions;

internal sealed class SessionRetentionDispatcherService(
    ISessionRetentionQueue queue,
    IGatewaySessionStore sessionStore,
    MafSessionStore mafSessionStore,
    ILogger<SessionRetentionDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (SessionRetentionWork work in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {

                using IDisposable scope = logger.BeginKautukaCorrelationScope(
                    ("TargetType", work.TargetType),
                    ("TargetId", work.TargetId),
                    ("OwnerUserId", work.OwnerUserId),
                    ("RetainCount", work.RetainCount),
                    ("SessionId", work.SessionId));

                try
                {
                    IReadOnlyList<string> deletedSessionIds = sessionStore.PruneSessions(work.TargetType, work.TargetId, work.OwnerUserId, work.RetainCount, work.SessionId);
                    if (deletedSessionIds.Count == 0)
                    {
                        continue;
                    }

                    await mafSessionStore.DeleteManyAsync(deletedSessionIds, stoppingToken).ConfigureAwait(false);
                    SessionRetentionLog.RetentionPruned(logger, work.TargetType, work.TargetId, deletedSessionIds.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SessionRetentionLog.RetentionCleanupFailed(logger, ex, work.TargetType, work.TargetId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
