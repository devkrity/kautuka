using System.Diagnostics;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Observability;

using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed class ChannelApprovalRequestedHandler(
    IChannelIngressService ingress,
    ILogger<ChannelApprovalRequestedHandler> logger) : INotificationHandler<HitlApprovalRequested>
{
    public async ValueTask Handle(HitlApprovalRequested notification, CancellationToken cancellationToken)
    {
        using Activity? activity = AgentRunHostCallTracer.StartScopedInternalActivity("hitl approval notify channel");
        activity?.SetTag("hitl.request.id", notification.RequestId);
        activity?.SetTag("hitl.target.type", notification.TargetType);
        activity?.SetTag("hitl.target.id", notification.TargetId);

        HitlEventHandlersLog.ChannelApprovalHandlerInvoked(logger, notification.RequestId, notification.UserId, notification.SessionId);

        if (string.IsNullOrWhiteSpace(notification.UserId))
        {
            HitlEventHandlersLog.ChannelApprovalSkippedNoUserId(logger, notification.RequestId);
            return;
        }

        var request = new GatewayChatApprovalRequest(
            notification.RequestId,
            notification.ToolCallId,
            notification.ToolName,
            notification.ArgumentsJson,
            RunId: notification.RunId,
            ToolType: notification.ToolType,
            OuterSessionId: notification.OuterSessionId,
            OuterAgentId: notification.OuterAgentId,
            TraceId: Activity.Current?.TraceId.ToString(),
            AgentId: string.Equals(notification.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                ? notification.TargetId
                : null,
            SessionId: notification.SessionId);

        HitlEventHandlersLog.ChannelApprovalNotifying(logger, notification.RequestId, notification.UserId, notification.TargetType, notification.TargetId);

        try
        {
            await ingress.NotifyChannelApprovalAsync(
                notification.UserId,
                notification.SessionId,
                notification.TargetType,
                notification.TargetId,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HitlEventHandlersLog.ChannelApprovalNotifyFailed(logger, ex, notification.RequestId);
            return;
        }

        HitlEventHandlersLog.ChannelApprovalNotifyCompleted(logger, notification.RequestId);
    }
}

internal sealed class ChannelApprovalResolvedHandler(IChannelIngressService ingress) : INotificationHandler<HitlApprovalResolved>
{
    // Hot-path adapter: the handler only forwards a Task-returning call and does not need async state machine allocation.
    public ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
        => new(ingress.SyncChannelApprovalResolutionAsync(
            new ChannelApprovalResolution(
                notification.RequestId,
                ApprovalActionCatalog.Resolve(
                    notification.Approved,
                    notification.Source == HitlResolutionSource.SystemTimeout,
                    notification.Reason,
                    notification.StopRun,
                    notification.TrustThisRun),
                notification.DecidedByUserId),
            cancellationToken));
}

internal sealed class ApprovalContinuationEnqueueHandler(
    IApprovalContinuationQueue queue,
    IGatewayApprovalStore approvalStore,
    IAgentToolApprovalWaiter agentToolApprovalWaiter,
    IAgentToolInvocationLinkStore agentToolInvocationLinkStore,
    IRunWaitStateStore runWaitStateStore,
    ILogger<ApprovalContinuationEnqueueHandler> logger) : INotificationHandler<HitlApprovalResolved>
{
    public async ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
    {
        if (agentToolApprovalWaiter.IsWaiting(notification.RequestId))
        {
            return;
        }

        GatewayToolApprovalRequest? request = approvalStore.GetById(notification.RequestId);
        if (request is null
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal))
        {
            HitlEventHandlersLog.SkippingQueueEnqueueForMissingRequest(logger, notification.RequestId);
            return;
        }

        // In-process AAAT await owns nested HITL when a link exists without a durable parent
        // waiting_on_child pause. Do not rely only on IsWaiting — approve can race registration.
        if (IsOwnedByInProcessAgentToolAwait(notification.RequestId))
        {
            return;
        }

        // Browser approve/deny of outer chat resumes via ResumeWaitingRoot while the run is
        // still paused. Stop-run publishes run.stop.requested first, so that resume path
        // no longer sees a waiting root and this handler must enqueue the StopRun continuation.
        if (notification.Source == HitlResolutionSource.Browser
            && string.IsNullOrWhiteSpace(request.OuterSessionId)
            && !notification.StopRun)
        {
            return;
        }

        RunWaitState? packedWait = PackedToolApproval.FindActiveWait(
            runWaitStateStore,
            notification.RequestId,
            continuationRunId: null);
        IReadOnlyList<string> packedIds = PackedToolApproval.ReadPackedRequestIds(
            packedWait,
            notification.RequestId);
        if (packedIds.Count > 1
            && PackedToolApproval.HasUndecidedHumanSiblings(
                approvalStore,
                packedIds,
                notification.RequestId))
        {
            return;
        }

        using Activity? activity = AgentRunHostCallTracer.StartScopedInternalActivity("hitl approval enqueue continuation");
        activity?.SetTag("hitl.request.id", notification.RequestId);
        activity?.SetTag("hitl.approved", notification.Approved);
        activity?.SetTag("hitl.source", notification.Source.ToString());
        activity?.SetTag(
            "hitl.agent_tool_invocation_linked",
            agentToolInvocationLinkStore.GetByApprovalRequestId(notification.RequestId) is not null);

        string? targetType = null;
        string? targetId = null;
        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            targetType = "agent";
            targetId = request.AgentId;
        }

        targetType ??= notification.TargetType;
        targetId ??= notification.TargetId;

        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
        {
            HitlEventHandlersLog.SkippingQueueEnqueueForMissingTarget(logger, notification.RequestId);
            return;
        }

        HitlEventHandlersLog.EnqueueingContinuation(
            logger,
            notification.RequestId,
            request.SessionId,
            targetType,
            targetId,
            notification.Source,
            notification.Approved);

        await queue.EnqueueAsync(new ApprovalContinuationWork(
            notification.RequestId,
            request.SessionId,
            targetType,
            targetId,
            request.UserId ?? notification.UserId,
            notification.Approved,
            notification.Source,
            notification.Reason,
            notification.DecidedByUserId,
            StopRun: notification.StopRun),
            cancellationToken).ConfigureAwait(false);
    }

    private bool IsOwnedByInProcessAgentToolAwait(string requestId)
    {
        AgentToolInvocationLink? link = agentToolInvocationLinkStore.GetByApprovalRequestId(requestId);
        if (link is null
            || string.IsNullOrWhiteSpace(link.CallerSessionId)
            || string.IsNullOrWhiteSpace(link.CallerToolCallId))
        {
            return false;
        }

        return runWaitStateStore.GetActiveChildPauseForCaller(link.CallerSessionId, link.CallerToolCallId) is null;
    }
}

internal sealed class ChannelContinuationResultHandler : INotificationHandler<HitlContinuationCompleted>
{
    // Continuation text is delivered by ChannelHost from projected runtime events
    // (assistant.message.completed / run.finished). Gateway no longer gates by tether mode.
    public ValueTask Handle(HitlContinuationCompleted notification, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class HitlAuditLogHandler(ILogger<HitlAuditLogHandler> logger) :
    INotificationHandler<HitlApprovalRequested>,
    INotificationHandler<HitlApprovalResolved>,
    INotificationHandler<HitlContinuationCompleted>,
    INotificationHandler<HitlContinuationFailed>
{
    public ValueTask Handle(HitlApprovalRequested notification, CancellationToken cancellationToken)
    {
        HitlEventHandlersLog.HitlRequested(
            logger,
            notification.RequestId,
            notification.TargetType,
            notification.TargetId,
            notification.SessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
    {
        HitlEventHandlersLog.HitlResolved(
            logger,
            notification.RequestId,
            notification.Approved,
            notification.Source);
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(HitlContinuationCompleted notification, CancellationToken cancellationToken)
    {
        HitlEventHandlersLog.HitlContinuationCompleted(
            logger,
            notification.RequestId,
            notification.Approved);
        return ValueTask.CompletedTask;
    }

    public ValueTask Handle(HitlContinuationFailed notification, CancellationToken cancellationToken)
    {
        HitlEventHandlersLog.HitlContinuationFailed(
            logger,
            notification.RequestId,
            notification.Error);
        return ValueTask.CompletedTask;
    }
}

internal static partial class HitlEventHandlersLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Skipping queue enqueue for request {RequestId}; request or session missing")]
    public static partial void SkippingQueueEnqueueForMissingRequest(ILogger logger, string requestId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Channel approval handler invoked for request {RequestId} userId={UserId} sessionId={SessionId}")]
    public static partial void ChannelApprovalHandlerInvoked(ILogger logger, string requestId, string? userId, string sessionId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Channel approval skipped for request {RequestId}; UserId is null or empty")]
    public static partial void ChannelApprovalSkippedNoUserId(ILogger logger, string requestId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Notifying channel of approval request {RequestId} userId={UserId} targetType={TargetType} targetId={TargetId}")]
    public static partial void ChannelApprovalNotifying(ILogger logger, string requestId, string userId, string targetType, string targetId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Channel approval notify completed for request {RequestId}")]
    public static partial void ChannelApprovalNotifyCompleted(ILogger logger, string requestId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Channel approval notify failed for request {RequestId}; web HITL pause is unchanged")]
    public static partial void ChannelApprovalNotifyFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipping queue enqueue for request {RequestId}; target missing")]
    public static partial void SkippingQueueEnqueueForMissingTarget(ILogger logger, string requestId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "HITL requested {RequestId} for {TargetType}:{TargetId} session={SessionId}")]
    public static partial void HitlRequested(ILogger logger, string requestId, string targetType, string targetId, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "HITL resolved {RequestId} approved={Approved} source={Source}")]
    public static partial void HitlResolved(ILogger logger, string requestId, bool approved, HitlResolutionSource source);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "HITL continuation completed {RequestId} approved={Approved}")]
    public static partial void HitlContinuationCompleted(ILogger logger, string requestId, bool approved);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "HITL continuation failed {RequestId}: {Error}")]
    public static partial void HitlContinuationFailed(ILogger logger, string requestId, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Enqueueing approval continuation request {RequestId} for session {SessionId}, target {TargetType}:{TargetId}; source={Source}, approved={Approved}")]
    public static partial void EnqueueingContinuation(ILogger logger, string requestId, string sessionId, string targetType, string targetId, HitlResolutionSource source, bool approved);

}
