using Kautuka.Gateway.Domain.Execution;

using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// Watch doorbell for <c>/api/approvals/changes</c>. Session-less HITL (Auto-Pilot pre-run)
/// never writes a Narad interrupt journal; in-session REST cards also miss mid-pack
/// <c>interrupt.resolved</c>, so both surfaces need this ping. The ping is not a journal
/// fact and must not wear a Narad event type.
/// </summary>
internal sealed class ApprovalStreamInvalidationHandler(IRuntimeEventStreamHub streamHub) :
    INotificationHandler<HitlApprovalRequested>,
    INotificationHandler<HitlApprovalResolved>
{
    internal const string WatchEventType = "hitl.invalidation";

    public ValueTask Handle(HitlApprovalRequested notification, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(notification.UserId))
        {
            return ValueTask.CompletedTask;
        }

        streamHub.PublishApprovalInvalidation(
            notification.UserId,
            notification.TargetId,
            notification.RequestId,
            $"hitl-invalidation:{notification.RequestId}:requested",
            WatchEventType,
            string.IsNullOrWhiteSpace(notification.SessionId) ? null : notification.SessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(notification.UserId))
        {
            return ValueTask.CompletedTask;
        }

        streamHub.PublishApprovalInvalidation(
            notification.UserId,
            notification.TargetId,
            notification.RequestId,
            $"hitl-invalidation:{notification.RequestId}:resolved",
            WatchEventType,
            string.IsNullOrWhiteSpace(notification.SessionId) ? null : notification.SessionId);
        return ValueTask.CompletedTask;
    }
}
