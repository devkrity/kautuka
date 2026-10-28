using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Autopilot;

internal sealed class AutopilotRunExecutionService(
    IGatewayAutopilotStore autopilotStore,
    IChatGatewayService chatGatewayService,
    IGatewayWorkflowStore workflowStore,
    WorkflowInstanceLauncher workflowLauncher,
    IAutopilotNotificationDispatcher notificationDispatcher,
    ILogger<AutopilotRunExecutionService> logger) : IAutopilotRunExecutionService
{
    public async Task StartAndExecuteAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken)
    {
        using IDisposable scope = logger.BeginKautukaCorrelationScope(
            ("RunId", run.Id),
            ("ScheduleId", schedule.Id),
            ("TargetType", schedule.TargetType),
            ("TargetId", schedule.TargetId),
            ("OwnerUserId", schedule.OwnerUserId));

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        bool transitioned = autopilotStore.TransitionRunState(
            run.Id,
            run.Status,
            AutopilotRunStatuses.Running,
            error: null,
            outputJson: null,
            sessionId: run.SessionId,
            runtimeRunId: run.RuntimeRunId,
            preRunApprovalRequestId: run.PreRunApprovalRequestId,
            nowUtc: nowUtc,
            markStarted: true,
            markEnded: false);

        if (!transitioned)
        {
            return;
        }

        if (string.Equals(schedule.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal))
        {
            await StartWorkflowAsync(schedule, run, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ExecuteRunAsync(schedule, run, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartWorkflowAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, CancellationToken cancellationToken)
    {
        GatewayWorkflowDefinition? definition = workflowStore.GetDefinition(schedule.TargetId);
        if (definition is null || !string.Equals(definition.OwnerUserId, schedule.OwnerUserId, StringComparison.Ordinal))
        {
            await FailWorkflowLaunchAsync(schedule, run, "Workflow is no longer available to this owner.", cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonObject input;
        try
        {
            input = JsonNode.Parse(schedule.SchedulePromptContext)?.AsObject()
                ?? throw new WorkflowSchemaException("Workflow start values must be a JSON object.");
            if (WorkflowSchemaSubset.DeclaresProperty(definition.InputSchema, "receivedAfter")
                && input["receivedAfter"] is null)
            {
                GatewayScheduledRun? previousCompleted = autopilotStore.ListRuns(
                    schedule.OwnerUserId, schedule.Id, 250, 0)
                    .FirstOrDefault(candidate => candidate.Id != run.Id
                        && (string.Equals(candidate.Status, AutopilotRunStatuses.Completed, StringComparison.Ordinal)
                            || string.Equals(candidate.Status, AutopilotRunStatuses.NoOp, StringComparison.Ordinal)));
                input["receivedAfter"] = previousCompleted?.ScheduledFor ?? schedule.CreatedAt;
            }
            WorkflowSchemaSubset.ValidateInstance(definition.InputSchema, input);
            WorkflowSchemaSubset.ValidateInstance(definition.WorkingSchema, input);
            WorkflowEnvelopeCanonical.EnsureSafe(input);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or WorkflowSchemaException)
        {
            await FailWorkflowLaunchAsync(schedule, run, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        WorkflowInstanceStartResult started = workflowLauncher.Start(
            definition,
            schedule.OwnerUserId,
            input,
            $"autopilot:{schedule.Id}",
            schedule.WorkspaceFilePaths);
        if (!started.Succeeded)
        {
            if (started.StatusCode == StatusCodes.Status409Conflict)
            {
                const string skipReason = "An overlapping workflow run is already in progress.";
                bool skipped = autopilotStore.TransitionRunState(
                    run.Id, AutopilotRunStatuses.Running, AutopilotRunStatuses.Skipped,
                    error: null, outputJson: null, sessionId: run.SessionId,
                    runtimeRunId: run.RuntimeRunId, preRunApprovalRequestId: run.PreRunApprovalRequestId,
                    nowUtc: DateTimeOffset.UtcNow, markStarted: false, markEnded: true);
                if (skipped)
                {
                    autopilotStore.SetRunSkipReason(run.Id, skipReason, DateTimeOffset.UtcNow);
                    if (string.Equals(schedule.OverlapPolicy, AutopilotOverlapPolicies.SkipAndNotify, StringComparison.Ordinal))
                    {
                        await notificationDispatcher.DispatchNotificationsAsync(
                            run with { Status = AutopilotRunStatuses.Skipped, SkipReason = skipReason }, schedule.Id,
                            null, $"Auto-Pilot workflow skipped: {skipReason}", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    }
                }
                return;
            }
            await FailWorkflowLaunchAsync(schedule, run, started.Error ?? "Couldn't start this workflow run.", cancellationToken).ConfigureAwait(false);
            return;
        }

        autopilotStore.UpdateRunMetadata(run.Id, null, started.Instance!.Id, run.PreRunApprovalRequestId, null, null, DateTimeOffset.UtcNow);
        await notificationDispatcher.DispatchNotificationsAsync(
            run with { RuntimeRunId = started.Instance.Id }, schedule.Id,
            "Auto-Pilot workflow started.", null, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailWorkflowLaunchAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, string error, CancellationToken cancellationToken)
    {
        bool failed = autopilotStore.TransitionRunState(
            run.Id, AutopilotRunStatuses.Running, AutopilotRunStatuses.Failed, error, null,
            run.SessionId, run.RuntimeRunId, run.PreRunApprovalRequestId, DateTimeOffset.UtcNow, false, true);
        if (failed)
        {
            await notificationDispatcher.DispatchNotificationsAsync(
                run, schedule.Id, null, $"Auto-Pilot workflow failed: {error}", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteRunAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        string? runtimeRunId = run.RuntimeRunId;
        string? finalResponse = null;

        JsonObject body = new()
        {
            ["target_type"] = schedule.TargetType,
            ["target_id"] = schedule.TargetId,
            ["message"] = schedule.SchedulePromptContext,
            ["workspaceFiles"] = JsonSerializer.SerializeToNode(schedule.WorkspaceFilePaths, KautukaJsonContext.Default.StringArray),
            ["user_id"] = schedule.OwnerUserId,
            ["source"] = "autopilot",
        };

        try
        {
            await foreach (NaradPersistedEvent evt in chatGatewayService.StreamChatAsync(
                JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject),
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(evt.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal))
                {
                    sessionId = evt.SessionId;
                    runtimeRunId = evt.RootRunId ?? evt.RunId ?? runtimeRunId;
                    autopilotStore.UpdateRunMetadata(run.Id, sessionId, runtimeRunId, run.PreRunApprovalRequestId, null, null, DateTimeOffset.UtcNow);
                    await notificationDispatcher.DispatchNotificationsAsync(
                        run with { SessionId = sessionId, RuntimeRunId = runtimeRunId }, schedule.Id,
                        startedMessage: "Auto-Pilot run started.", finalMessage: null,
                        DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // An interrupt is not the terminal pause. The paused user/tool turn
                // is persisted by ChatGatewayService before it yields run.paused.
                if (string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
                {
                    autopilotStore.TransitionRunState(
                        run.Id,
                        AutopilotRunStatuses.Running,
                        AutopilotRunStatuses.WaitingForInRunApproval,
                        error: null,
                        outputJson: null,
                        sessionId: sessionId,
                        runtimeRunId: runtimeRunId,
                        preRunApprovalRequestId: run.PreRunApprovalRequestId,
                        nowUtc: DateTimeOffset.UtcNow,
                        markStarted: false,
                        markEnded: false);

                    return;
                }

                if (string.Equals(evt.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                    || string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal))
                {
                    GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(
                        evt,
                        schedule.TargetType,
                        schedule.TargetId,
                        evt.SessionId);
                    finalResponse = done.Result.Response;
                    if (string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(finalResponse);
                    }
                    break;
                }
            }

            bool finalized = autopilotStore.TransitionRunState(
                run.Id,
                AutopilotRunStatuses.Running,
                string.IsNullOrWhiteSpace(finalResponse) ? AutopilotRunStatuses.NoOp : AutopilotRunStatuses.Completed,
                error: null,
                outputJson: finalResponse,
                sessionId: sessionId,
                runtimeRunId: runtimeRunId,
                preRunApprovalRequestId: run.PreRunApprovalRequestId,
                nowUtc: DateTimeOffset.UtcNow,
                markStarted: false,
                markEnded: true);

            if (finalized)
            {
                await notificationDispatcher.DispatchNotificationsAsync(
                    run with { SessionId = sessionId ?? run.SessionId, RuntimeRunId = runtimeRunId },
                    schedule.Id,
                    startedMessage: null,
                    finalMessage: string.IsNullOrWhiteSpace(finalResponse)
                        ? "Auto-Pilot run completed with no response content."
                        : finalResponse,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            autopilotStore.TransitionRunState(
                run.Id,
                AutopilotRunStatuses.Running,
                AutopilotRunStatuses.Failed,
                error: ex.Message,
                outputJson: null,
                sessionId: sessionId,
                runtimeRunId: runtimeRunId,
                preRunApprovalRequestId: run.PreRunApprovalRequestId,
                nowUtc: DateTimeOffset.UtcNow,
                markStarted: false,
                markEnded: true);

            await notificationDispatcher.DispatchNotificationsAsync(
                run with { SessionId = sessionId ?? run.SessionId, RuntimeRunId = runtimeRunId },
                schedule.Id,
                startedMessage: null,
                finalMessage: $"Auto-Pilot run failed: {ex.Message}",
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
