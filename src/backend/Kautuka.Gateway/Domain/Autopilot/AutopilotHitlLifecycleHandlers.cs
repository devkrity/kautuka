using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

namespace Kautuka.Gateway.Domain.Autopilot;

internal sealed class AutopilotApprovalRequestedHandler(
    IGatewayAutopilotStore autopilotStore,
    IGatewayApprovalStore approvalStore,
    IExecutionQuarantineStore quarantineStore) : INotificationHandler<HitlApprovalRequested>
{
    public ValueTask Handle(HitlApprovalRequested notification, CancellationToken cancellationToken)
    {
        if (string.Equals(notification.ToolType, AutopilotLaunchPolicy.PreRunToolType, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        GatewayScheduledRun? run = AutopilotHitlCorrelation.ResolveRun(notification.RequestId, autopilotStore, approvalStore, quarantineStore);
        if (run is null)
        {
            return ValueTask.CompletedTask;
        }

        // Best effort transition for in-run HITL waits.
        autopilotStore.TransitionRunState(
            run.Id,
            AutopilotRunStatuses.Running,
            AutopilotRunStatuses.WaitingForInRunApproval,
            error: null,
            outputJson: null,
            sessionId: run.SessionId,
            runtimeRunId: run.RuntimeRunId,
            preRunApprovalRequestId: run.PreRunApprovalRequestId,
            nowUtc: DateTimeOffset.UtcNow,
            markStarted: false,
            markEnded: false);

        return ValueTask.CompletedTask;
    }

}

internal sealed class AutopilotContinuationCompletedHandler(
    IGatewayAutopilotStore autopilotStore,
    IGatewayApprovalStore approvalStore,
    IExecutionQuarantineStore quarantineStore) : INotificationHandler<HitlContinuationCompleted>
{
    public ValueTask Handle(HitlContinuationCompleted notification, CancellationToken cancellationToken)
    {
        GatewayScheduledRun? run = AutopilotHitlCorrelation.ResolveRun(notification.RequestId, autopilotStore, approvalStore, quarantineStore);
        if (run is null)
        {
            return ValueTask.CompletedTask;
        }

        if (notification.HasNestedApprovalRequest)
        {
            return ValueTask.CompletedTask;
        }

        autopilotStore.TransitionRunState(
            run.Id,
            AutopilotRunStatuses.WaitingForInRunApproval,
            AutopilotRunStatuses.Completed,
            error: null,
            outputJson: notification.OutputText,
            sessionId: run.SessionId,
            runtimeRunId: run.RuntimeRunId,
            preRunApprovalRequestId: run.PreRunApprovalRequestId,
            nowUtc: DateTimeOffset.UtcNow,
            markStarted: false,
            markEnded: true);

        return ValueTask.CompletedTask;
    }

}

internal sealed class AutopilotContinuationFailedHandler(
    IGatewayAutopilotStore autopilotStore,
    IGatewayApprovalStore approvalStore,
    IExecutionQuarantineStore quarantineStore) : INotificationHandler<HitlContinuationFailed>
{
    public ValueTask Handle(HitlContinuationFailed notification, CancellationToken cancellationToken)
    {
        GatewayScheduledRun? run = AutopilotHitlCorrelation.ResolveRun(notification.RequestId, autopilotStore, approvalStore, quarantineStore);
        if (run is null)
        {
            return ValueTask.CompletedTask;
        }

        autopilotStore.TransitionRunState(
            run.Id,
            AutopilotRunStatuses.WaitingForInRunApproval,
            AutopilotRunStatuses.Failed,
            error: notification.Error,
            outputJson: null,
            sessionId: run.SessionId,
            runtimeRunId: run.RuntimeRunId,
            preRunApprovalRequestId: run.PreRunApprovalRequestId,
            nowUtc: DateTimeOffset.UtcNow,
            markStarted: false,
            markEnded: true);

        return ValueTask.CompletedTask;
    }

}

internal static class AutopilotHitlCorrelation
{
    public static GatewayScheduledRun? ResolveRun(
        string requestId,
        IGatewayAutopilotStore autopilotStore,
        IGatewayApprovalStore approvalStore,
        IExecutionQuarantineStore quarantineStore)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        GatewayToolApprovalRequest? approval = approvalStore.GetById(requestId);
        if (string.IsNullOrWhiteSpace(approval?.RunId))
        {
            quarantineStore.Quarantine(
                "autopilot_hitl",
                requestId,
                "missing_approval_run_correlation",
                "The approval has no exact scheduled or runtime run identity.");
            return null;
        }

        GatewayScheduledRun? run = autopilotStore.GetRunById(approval.RunId)
            ?? autopilotStore.GetRunByRuntimeRunId(approval.RunId)
            ?? ResolveRunBySession(autopilotStore, approval.SessionId)
            ?? ResolveRunBySession(autopilotStore, approval.OuterSessionId);
        if (run is null)
        {
            quarantineStore.Quarantine(
                "autopilot_hitl",
                requestId,
                "scheduled_run_not_found",
                "No scheduled run exactly matches the approval run identity.");
            return null;
        }

        if (AutopilotRunStatuses.IsTerminal(run.Status))
        {
            quarantineStore.Quarantine(
                "autopilot_hitl",
                requestId,
                "scheduled_run_already_terminal",
                "The exactly correlated scheduled run is already terminal.");
            return null;
        }

        quarantineStore.Resolve("autopilot_hitl", requestId);
        return run;
    }

    private static GatewayScheduledRun? ResolveRunBySession(IGatewayAutopilotStore autopilotStore, string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId)
            ? null
            : autopilotStore.GetLatestNonTerminalRunBySession(sessionId);
}
