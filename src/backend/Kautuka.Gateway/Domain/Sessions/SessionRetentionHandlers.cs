using Mediator;

namespace Kautuka.Gateway.Domain.Sessions;

internal sealed class SessionRetentionEnqueueHandler(
    ISessionRetentionQueue queue,
    ILogger<SessionRetentionEnqueueHandler> logger) : INotificationHandler<SessionRetentionRequested>
{
    public ValueTask Handle(SessionRetentionRequested notification, CancellationToken cancellationToken)
    {
        if (notification.RetainCount <= 0)
        {
            return ValueTask.CompletedTask;
        }

        bool enqueued = queue.TryEnqueue(new SessionRetentionWork(
            notification.TargetType,
            notification.TargetId,
            notification.OwnerUserId,
            notification.SessionId,
            notification.RetainCount));

        if (!enqueued)
        {
            SessionRetentionLog.RetentionQueueFull(logger, notification.TargetType, notification.TargetId, notification.SessionId);
        }

        return ValueTask.CompletedTask;
    }
}

internal static partial class SessionRetentionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Skipping session retention cleanup enqueue for {TargetType}:{TargetId} session {SessionId} because the queue is full")]
    public static partial void RetentionQueueFull(ILogger logger, string targetType, string targetId, string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Pruned {DeletedCount} retained sessions for {TargetType}:{TargetId}")]
    public static partial void RetentionPruned(ILogger logger, string targetType, string targetId, int deletedCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Session retention cleanup failed for {TargetType}:{TargetId}")]
    public static partial void RetentionCleanupFailed(ILogger logger, Exception exception, string targetType, string targetId);
}
