using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

namespace Kautuka.Gateway.Domain.Autopilot;

internal sealed class AutopilotRunLaunchService(
    IGatewayAutopilotStore autopilotStore,
    IGatewayApprovalStore approvalStore,
    IAutopilotRunExecutionService runExecutionService,
    IMediator mediator,
    ILogger<AutopilotRunLaunchService> logger,
    IGatewayWorkflowStore workflowStore) : IAutopilotRunLaunchService
{
    public async Task LaunchAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken)
    {
        GatewayScheduledRun current = autopilotStore.GetRunById(run.Id) ?? run;
        if (AutopilotRunStatuses.IsTerminal(current.Status))
        {
            return;
        }

        if (!AutopilotLaunchPolicy.RequiresPreRunApproval(schedule))
        {
            await runExecutionService.StartAndExecuteAsync(schedule, current, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TryGetPreRunApproval(current, out GatewayToolApprovalRequest? approval) && approval is not null)
        {
            if (string.Equals(approval.Decision, "approved", StringComparison.OrdinalIgnoreCase))
            {
                await runExecutionService.StartAndExecuteAsync(schedule, current, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (approval.Decision is null)
            {
                await PublishApprovalRequestedAsync(schedule, current, approval, cancellationToken).ConfigureAwait(false);
                return;
            }

            AutopilotRunLaunchLog.PreRunApprovalAlreadyDecided(logger, current.Id, approval.Id, approval.Decision);
            return;
        }

        await RequestPreRunApprovalAsync(schedule, current, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemindPendingAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GatewayToolApprovalRequest> pending = ListPendingForRun(run);
        if (pending.Count == 0)
        {
            AutopilotRunLaunchLog.NoPendingApprovalToRemind(logger, run.Id, run.Status);
            return false;
        }

        foreach (GatewayToolApprovalRequest approval in pending)
        {
            await PublishApprovalRequestedAsync(schedule, run, approval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private bool TryGetPreRunApproval(GatewayScheduledRun run, out GatewayToolApprovalRequest? approval)
    {
        approval = null;
        if (string.IsNullOrWhiteSpace(run.PreRunApprovalRequestId))
        {
            return false;
        }

        approval = approvalStore.GetById(run.PreRunApprovalRequestId);
        return approval is not null;
    }

    private async Task RequestPreRunApprovalAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string requestId = $"apr-{Guid.NewGuid():N}";
        string? expiresAt = ResolvePreRunApprovalExpiry(schedule, nowUtc)?.ToString("O");
        var request = new GatewayToolApprovalRequest(
            Id: requestId,
            SessionId: null,
            RunId: run.Id,
            CheckpointId: null,
            AgentId: string.Equals(schedule.TargetType, "agent", StringComparison.Ordinal) ? schedule.TargetId : null,
            UserId: schedule.OwnerUserId,
            ToolCallId: AutopilotLaunchPolicy.PreRunToolCallId,
            ToolName: AutopilotLaunchPolicy.PreRunToolName,
            ToolType: AutopilotLaunchPolicy.PreRunToolType,
            McpServerId: null,
            ArgumentsJson: run.InputJson,
            RequestedAt: nowUtc.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: expiresAt,
            ChannelTetherMode: null);

        approvalStore.CreateRequest(request);

        bool transitioned = autopilotStore.TransitionRunState(
            run.Id,
            run.Status,
            AutopilotRunStatuses.AwaitingPreRunApproval,
            error: null,
            outputJson: null,
            sessionId: run.SessionId,
            runtimeRunId: run.RuntimeRunId,
            preRunApprovalRequestId: requestId,
            nowUtc: nowUtc,
            markStarted: false,
            markEnded: false);

        if (!transitioned)
        {
            AutopilotRunLaunchLog.PreRunTransitionFailed(logger, run.Id, run.Status);
            return;
        }

        GatewayScheduledRun awaiting = autopilotStore.GetRunById(run.Id) ?? run with
        {
            Status = AutopilotRunStatuses.AwaitingPreRunApproval,
            PreRunApprovalRequestId = requestId,
        };
        await PublishApprovalRequestedAsync(schedule, awaiting, request, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListPendingForRun(GatewayScheduledRun run)
    {
        GatewayWorkflowInstance? workflowInstance = string.Equals(run.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(run.RuntimeRunId)
                ? workflowStore.GetInstance(run.RuntimeRunId)
                : null;
        var pending = new Dictionary<string, GatewayToolApprovalRequest>(StringComparer.Ordinal);
        void Consider(GatewayToolApprovalRequest? approval)
        {
            if (approval is null || approval.Decision is not null || string.IsNullOrWhiteSpace(approval.Id))
            {
                return;
            }

            pending[approval.Id] = approval;
        }

        if (!string.IsNullOrWhiteSpace(run.PreRunApprovalRequestId))
        {
            Consider(approvalStore.GetById(run.PreRunApprovalRequestId));
        }

        foreach (GatewayToolApprovalRequest approval in approvalStore.ListPending(run.OwnerUserId, adminAll: false))
        {
            bool isWorkflowRun = string.Equals(run.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal);
            bool belongsToWorkflow = workflowInstance is not null
                && (string.Equals(approval.InstanceId, workflowInstance.Id, StringComparison.Ordinal)
                    || workflowInstance.Steps.Any(step =>
                        (!string.IsNullOrWhiteSpace(approval.RunId)
                            && string.Equals(step.RunId, approval.RunId, StringComparison.Ordinal))
                        || (!string.IsNullOrWhiteSpace(approval.SessionId)
                            && !string.IsNullOrWhiteSpace(step.SessionId)
                            && string.Equals(step.SessionId, approval.SessionId, StringComparison.Ordinal))
                        || (!string.IsNullOrWhiteSpace(approval.OuterSessionId)
                            && !string.IsNullOrWhiteSpace(step.SessionId)
                            && string.Equals(step.SessionId, approval.OuterSessionId, StringComparison.Ordinal))));
            if (isWorkflowRun
                ? belongsToWorkflow
                : string.Equals(approval.RunId, run.Id, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(run.RuntimeRunId)
                    && string.Equals(approval.RunId, run.RuntimeRunId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(run.SessionId)
                    && (string.Equals(approval.SessionId, run.SessionId, StringComparison.Ordinal)
                        || string.Equals(approval.OuterSessionId, run.SessionId, StringComparison.Ordinal))))
            {
                Consider(approval);
            }
        }

        return [.. pending.Values];
    }

    private async Task PublishApprovalRequestedAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        GatewayToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        bool preRun = string.Equals(request.ToolType, AutopilotLaunchPolicy.PreRunToolType, StringComparison.Ordinal);
        await mediator.Publish(new HitlApprovalRequested(
            RequestId: request.Id,
            ToolCallId: request.ToolCallId,
            ToolName: request.ToolName,
            ArgumentsJson: request.ArgumentsJson ?? run.InputJson,
            SessionId: preRun ? string.Empty : request.SessionId ?? string.Empty,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            UserId: request.UserId ?? schedule.OwnerUserId,
            RunId: request.RunId ?? run.Id,
            ToolType: request.ToolType ?? AutopilotLaunchPolicy.PreRunToolType,
            OuterSessionId: request.OuterSessionId,
            OuterAgentId: request.OuterAgentId,
            ExpiresAt: request.ExpiresAt), cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? ResolvePreRunApprovalExpiry(GatewayAutopilotSchedule schedule, DateTimeOffset nowUtc) =>
        schedule.PreRunApprovalExpiryPolicy switch
        {
            AutopilotPreRunApprovalExpiryPolicies.NextOccurrence =>
                AutopilotCron.GetNextOccurrence(schedule.CronExpression, ResolveTimeZone(schedule.TimeZoneId), nowUtc),
            AutopilotPreRunApprovalExpiryPolicies.Duration when schedule.PreRunApprovalTimeoutSeconds.HasValue =>
                nowUtc.AddSeconds(schedule.PreRunApprovalTimeoutSeconds.Value),
            AutopilotPreRunApprovalExpiryPolicies.Never => null,
            _ => throw new InvalidOperationException($"Invalid pre-run approval expiry policy '{schedule.PreRunApprovalExpiryPolicy}'."),
        };

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

internal static partial class AutopilotRunLaunchLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Auto-Pilot run {RunId} could not enter awaiting pre-run approval from {CurrentStatus}")]
    public static partial void PreRunTransitionFailed(ILogger logger, string runId, string currentStatus);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Auto-Pilot run {RunId} pre-run approval {RequestId} already decided as {Decision}")]
    public static partial void PreRunApprovalAlreadyDecided(ILogger logger, string runId, string requestId, string? decision);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Auto-Pilot run {RunId} is {Status} but no pending approval exists to remind")]
    public static partial void NoPendingApprovalToRemind(ILogger logger, string runId, string status);
}
