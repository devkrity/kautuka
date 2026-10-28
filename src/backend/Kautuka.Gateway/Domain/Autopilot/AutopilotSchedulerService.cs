using System.Text.Json;

using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

namespace Kautuka.Gateway.Domain.Autopilot;

internal sealed class AutopilotSchedulerService(
    IGatewayAutopilotStore autopilotStore,
    IGatewayApprovalStore approvalStore,
    IGatewayUserStore userStore,
    IAutopilotRunLaunchService runLaunchService,
    IAutopilotNotificationDispatcher notificationDispatcher,
    IGatewayWorkflowStore workflowStore,
    ILogger<AutopilotSchedulerService> logger,
    IApprovalResolutionService? approvalResolutionService = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunSafelyAsync(logger, "startup workflow synchronization", () => SyncWorkflowRunsAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
            await RunSafelyAsync(logger, "startup run reconciliation", () => ReconcileNonTerminalRunsAsync(stoppingToken), stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!stoppingToken.IsCancellationRequested
                && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunSafelyAsync(logger, "scheduled cycle", () => RunCycleOnceAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal static async Task RunSafelyAsync(
        ILogger logger,
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            AutopilotSchedulerLog.OperationFailed(logger, exception, operation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AutopilotSchedulerLog.OperationFailed(logger, exception, operation);
        }
    }

    internal async Task RunCycleOnceAsync(CancellationToken cancellationToken)
    {
        await SyncWorkflowRunsAsync(cancellationToken).ConfigureAwait(false);
        await ProcessDueSchedulesAsync(cancellationToken).ConfigureAwait(false);
        await ProcessAwaitingPreRunApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessDueSchedulesAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<GatewayAutopilotSchedule> schedules = autopilotStore.LeaseDueSchedules(
            leaseOwner: Environment.MachineName,
            nowUtc: nowUtc,
            leaseDuration: TimeSpan.FromMinutes(2),
            limit: 50);

        foreach (GatewayAutopilotSchedule schedule in schedules)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await ProcessDueScheduleAsync(schedule, nowUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessDueScheduleAsync(
        GatewayAutopilotSchedule schedule,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {

        using IDisposable scope = logger.BeginKautukaCorrelationScope(
            ("ScheduleId", schedule.Id),
            ("TargetType", schedule.TargetType),
            ("TargetId", schedule.TargetId),
            ("OwnerUserId", schedule.OwnerUserId));

        try
        {
            if (!TryGetActiveOwner(schedule.OwnerUserId, out _))
            {
                autopilotStore.RecordSuppression(new GatewayAutopilotSuppression(
                    Id: $"suppress-{Guid.NewGuid():N}",
                    ScheduleId: schedule.Id,
                    SuppressedAt: nowUtc.ToString("O"),
                    Reason: "inactive_owner",
                    ExistingRunId: null,
                    Note: "Suppressed because the schedule owner account is missing or inactive."));

                UpdateScheduleAfterEvaluation(schedule, nowUtc);
                return;
            }

            GatewayScheduledRun? latestRun = autopilotStore.GetLatestNonTerminalRunBySchedule(schedule.Id);
            if (latestRun is { Status: AutopilotRunStatuses.AwaitingPreRunApproval, PreRunApprovalRequestId: not null })
            {
                GatewayToolApprovalRequest? latestApproval = approvalStore.GetById(latestRun.PreRunApprovalRequestId);
                if (latestApproval is not null
                    && latestApproval.Decision is null
                    && DateTimeOffset.TryParse(latestApproval.ExpiresAt, out DateTimeOffset expiresAt)
                    && expiresAt <= nowUtc)
                {
                    await ExpireApprovalAsync(latestRun.PreRunApprovalRequestId, nowUtc, cancellationToken).ConfigureAwait(false);
                }

                latestRun = autopilotStore.GetLatestNonTerminalRunBySchedule(schedule.Id);
                if (latestRun is { Status: AutopilotRunStatuses.AwaitingPreRunApproval, PreRunApprovalRequestId: not null }
                    && string.Equals(approvalStore.GetById(latestRun.PreRunApprovalRequestId)?.Decision, "expired", StringComparison.Ordinal))
                {
                    _ = autopilotStore.TransitionRunState(
                        latestRun.Id, latestRun.Status, AutopilotRunStatuses.TimedOut, "Pre-run approval expired.", null,
                        latestRun.SessionId, latestRun.RuntimeRunId, latestRun.PreRunApprovalRequestId, nowUtc, false, true);
                    latestRun = autopilotStore.GetLatestNonTerminalRunBySchedule(schedule.Id);
                }
            }

            if (latestRun is not null && !AutopilotRunStatuses.IsTerminal(latestRun.Status))
            {
                if (string.Equals(latestRun.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(latestRun.RuntimeRunId)
                    && workflowStore.GetInstance(latestRun.RuntimeRunId) is { } linkedWorkflow
                    && !WorkflowInstanceStatuses.IsTerminal(linkedWorkflow.Status))
                {
                    bool waitingForDecision = string.Equals(linkedWorkflow.Status, WorkflowInstanceStatuses.Paused, StringComparison.Ordinal)
                        || string.Equals(
                            AutopilotRunStatuses.GetStateCategory(latestRun.Status),
                            AutopilotRunStateCategories.PendingApproval,
                            StringComparison.Ordinal);
                    if (waitingForDecision)
                    {
                        bool reminded = await runLaunchService.RemindPendingAsync(schedule, latestRun, cancellationToken).ConfigureAwait(false);
                        if (!reminded)
                        {
                            await notificationDispatcher.DispatchNotificationsAsync(
                                latestRun, schedule.Id, null,
                                "Auto-Pilot workflow is still waiting for a decision.", nowUtc, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        autopilotStore.RecordSuppression(new GatewayAutopilotSuppression(
                            Id: $"suppress-{Guid.NewGuid():N}",
                            ScheduleId: schedule.Id,
                            SuppressedAt: nowUtc.ToString("O"),
                            Reason: "workflow_instance_running",
                            ExistingRunId: latestRun.Id,
                            Note: "Suppressed because the workflow instance is still running."));
                    }

                    UpdateScheduleAfterEvaluation(schedule, nowUtc);
                    return;
                }

                if (string.Equals(
                    AutopilotRunStatuses.GetStateCategory(latestRun.Status),
                    AutopilotRunStateCategories.PendingApproval,
                    StringComparison.Ordinal))
                {
                    bool reminded = await runLaunchService.RemindPendingAsync(schedule, latestRun, cancellationToken).ConfigureAwait(false);
                    if (reminded)
                    {
                        UpdateScheduleAfterEvaluation(schedule, nowUtc);
                        return;
                    }

                    _ = autopilotStore.TransitionRunState(
                        latestRun.Id,
                        latestRun.Status,
                        AutopilotRunStatuses.Stale,
                        "Pending approval is missing; recovered so a new occurrence can launch.",
                        null,
                        latestRun.SessionId,
                        latestRun.RuntimeRunId,
                        latestRun.PreRunApprovalRequestId,
                        nowUtc,
                        false,
                        true);
                    latestRun = autopilotStore.GetLatestNonTerminalRunBySchedule(schedule.Id);
                }

                if (latestRun is not null && !AutopilotRunStatuses.IsTerminal(latestRun.Status))
                {
                    autopilotStore.RecordSuppression(new GatewayAutopilotSuppression(
                        Id: $"suppress-{Guid.NewGuid():N}",
                        ScheduleId: schedule.Id,
                        SuppressedAt: nowUtc.ToString("O"),
                        Reason: "non_terminal_run_exists",
                        ExistingRunId: latestRun.Id,
                        Note: "Suppressed because a non-terminal run already exists for this schedule."));

                    GatewayScheduledRun skippedRun = CreateSkippedRun(schedule, latestRun, nowUtc);
                    autopilotStore.InsertRun(skippedRun);
                    if (string.Equals(schedule.OverlapPolicy, AutopilotOverlapPolicies.SkipAndNotify, StringComparison.Ordinal))
                    {
                        await notificationDispatcher.DispatchNotificationsAsync(
                            skippedRun,
                            schedule.Id,
                            startedMessage: null,
                            finalMessage: $"Auto-Pilot occurrence skipped because run {latestRun.Id} is {latestRun.Status} ({AutopilotRunStatuses.GetStateCategory(latestRun.Status)}).",
                            nowUtc,
                            cancellationToken).ConfigureAwait(false);
                    }

                    UpdateScheduleAfterEvaluation(schedule, nowUtc);
                    return;
                }
            }

            var run = new GatewayScheduledRun(
                Id: $"srun-{Guid.NewGuid():N}",
                ScheduleId: schedule.Id,
                OwnerUserId: schedule.OwnerUserId,
                TargetType: schedule.TargetType,
                TargetId: schedule.TargetId,
                Status: AutopilotRunStatuses.Scheduled,
                TriggerType: "cron",
                ScheduledFor: nowUtc.ToString("O"),
                SessionId: null,
                RuntimeRunId: null,
                PreRunApprovalRequestId: null,
                InputJson: JsonSerializer.Serialize(AutopilotRunInput.Create(schedule, workflowStore), KautukaJsonContext.Default.JsonObject),
                OutputJson: null,
                Error: null,
                StateCategory: AutopilotRunStateCategories.Executing,
                SkipReason: null,
                BlockingRunId: null,
                BlockingRunStatusSnapshot: null,
                BlockingRunStateCategory: null,
                BlockedAt: null,
                CreatedAt: nowUtc.ToString("O"),
                UpdatedAt: nowUtc.ToString("O"),
                StartedAt: null,
                EndedAt: null);

            if (!autopilotStore.TryInsertRunUnlessNonTerminal(run, out GatewayScheduledRun? concurrentBlocker)
                && concurrentBlocker is not null)
            {
                GatewayScheduledRun skippedRun = CreateSkippedRun(schedule, concurrentBlocker, nowUtc);
                autopilotStore.InsertRun(skippedRun);
                if (string.Equals(schedule.OverlapPolicy, AutopilotOverlapPolicies.SkipAndNotify, StringComparison.Ordinal))
                {
                    await notificationDispatcher.DispatchNotificationsAsync(
                        skippedRun,
                        schedule.Id,
                        null,
                        $"Auto-Pilot occurrence skipped because run {concurrentBlocker.Id} is {concurrentBlocker.Status} ({AutopilotRunStatuses.GetStateCategory(concurrentBlocker.Status)}).",
                        nowUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                UpdateScheduleAfterEvaluation(schedule, nowUtc);
                return;
            }

            UpdateScheduleAfterEvaluation(schedule, nowUtc);

            await runLaunchService.LaunchAsync(schedule, run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AutopilotSchedulerLog.ScheduleEvaluationFailed(logger, ex, schedule.Id);
        }
    }

    private async Task ProcessAwaitingPreRunApprovalsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GatewayScheduledRun> awaitingRuns = autopilotStore.ListRunsByStatus(AutopilotRunStatuses.AwaitingPreRunApproval, 100);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        foreach (GatewayScheduledRun run in awaitingRuns)
        {

            using IDisposable scope = logger.BeginKautukaCorrelationScope(
                ("RunId", run.Id),
                ("ScheduleId", run.ScheduleId),
                ("PreRunApprovalRequestId", run.PreRunApprovalRequestId));

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(run.PreRunApprovalRequestId))
                {
                    continue;
                }

                GatewayToolApprovalRequest? approval = approvalStore.GetById(run.PreRunApprovalRequestId);
                if (approval is not null
                    && approval.Decision is null
                    && DateTimeOffset.TryParse(approval.ExpiresAt, out DateTimeOffset expiresAt)
                    && expiresAt <= nowUtc)
                {
                    await ExpireApprovalAsync(approval.Id, nowUtc, cancellationToken).ConfigureAwait(false);
                    approval = approvalStore.GetById(run.PreRunApprovalRequestId);
                }

                if (approval?.Decision is null)
                {
                    if (TryGetRunOwner(run, out GatewayUser? owner) && owner is not null)
                    {
                        continue;
                    }

                    bool transitionedInactive = autopilotStore.TransitionRunState(
                        run.Id,
                        AutopilotRunStatuses.AwaitingPreRunApproval,
                        AutopilotRunStatuses.Skipped,
                        error: "Owner account is inactive.",
                        outputJson: null,
                        sessionId: run.SessionId,
                        runtimeRunId: run.RuntimeRunId,
                        preRunApprovalRequestId: run.PreRunApprovalRequestId,
                        nowUtc: nowUtc,
                        markStarted: false,
                        markEnded: true);

                    if (transitionedInactive)
                    {
                        await ExpireApprovalAsync(run.PreRunApprovalRequestId, nowUtc, cancellationToken).ConfigureAwait(false);
                        await notificationDispatcher.DispatchNotificationsAsync(
                            run,
                            run.ScheduleId,
                            startedMessage: null,
                            finalMessage: "Auto-Pilot run was skipped because the owner account is inactive.",
                            nowUtc,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(approval.Decision, "expired", StringComparison.OrdinalIgnoreCase))
                {
                    bool timedOut = autopilotStore.TransitionRunState(
                        run.Id,
                        AutopilotRunStatuses.AwaitingPreRunApproval,
                        AutopilotRunStatuses.TimedOut,
                        error: approval.Reason ?? "Pre-run approval expired.",
                        outputJson: null,
                        sessionId: run.SessionId,
                        runtimeRunId: run.RuntimeRunId,
                        preRunApprovalRequestId: run.PreRunApprovalRequestId,
                        nowUtc: nowUtc,
                        markStarted: false,
                        markEnded: true);
                    if (timedOut)
                    {
                        await notificationDispatcher.DispatchNotificationsAsync(
                            run, run.ScheduleId, null, "Auto-Pilot run timed out waiting for pre-run approval.", nowUtc, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (string.Equals(approval.Decision, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    GatewayAutopilotSchedule? schedule = autopilotStore.GetSchedule(run.ScheduleId);
                    if (schedule is not null && TryGetActiveOwner(schedule.OwnerUserId, out _))
                    {
                        await runLaunchService.LaunchAsync(schedule, run, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    bool transitionedInactive = autopilotStore.TransitionRunState(
                        run.Id,
                        AutopilotRunStatuses.AwaitingPreRunApproval,
                        AutopilotRunStatuses.Skipped,
                        error: "Owner account is inactive.",
                        outputJson: null,
                        sessionId: run.SessionId,
                        runtimeRunId: run.RuntimeRunId,
                        preRunApprovalRequestId: run.PreRunApprovalRequestId,
                        nowUtc: nowUtc,
                        markStarted: false,
                        markEnded: true);

                    if (transitionedInactive)
                    {
                        await notificationDispatcher.DispatchNotificationsAsync(
                            run,
                            run.ScheduleId,
                            startedMessage: null,
                            finalMessage: "Auto-Pilot run was skipped because the owner account is inactive.",
                            nowUtc,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                bool transitioned = autopilotStore.TransitionRunState(
                    run.Id,
                    AutopilotRunStatuses.AwaitingPreRunApproval,
                    AutopilotRunStatuses.Rejected,
                    error: approval.Reason,
                    outputJson: null,
                    sessionId: run.SessionId,
                    runtimeRunId: run.RuntimeRunId,
                    preRunApprovalRequestId: run.PreRunApprovalRequestId,
                    nowUtc: nowUtc,
                    markStarted: false,
                    markEnded: true);

                if (transitioned)
                {
                    await notificationDispatcher.DispatchNotificationsAsync(
                        run,
                        run.ScheduleId,
                        startedMessage: null,
                        finalMessage: "Auto-Pilot run was rejected at pre-run approval.",
                        nowUtc,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "pre-run approval processing", run.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "pre-run approval processing", run.Id);
            }
        }
    }

    private GatewayScheduledRun CreateSkippedRun(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun blocker,
        DateTimeOffset nowUtc) =>
        new(
            Id: $"srun-{Guid.NewGuid():N}",
            ScheduleId: schedule.Id,
            OwnerUserId: schedule.OwnerUserId,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            Status: AutopilotRunStatuses.Skipped,
            TriggerType: "cron",
            ScheduledFor: nowUtc.ToString("O"),
            SessionId: null,
            RuntimeRunId: null,
            PreRunApprovalRequestId: null,
            InputJson: JsonSerializer.Serialize(AutopilotRunInput.Create(schedule, workflowStore), KautukaJsonContext.Default.JsonObject),
            OutputJson: null,
            Error: "Blocked by a non-terminal run.",
            StateCategory: AutopilotRunStateCategories.Terminal,
            SkipReason: "blocked_by_non_terminal_run",
            BlockingRunId: blocker.Id,
            BlockingRunStatusSnapshot: blocker.Status,
            BlockingRunStateCategory: AutopilotRunStatuses.GetStateCategory(blocker.Status),
            BlockedAt: nowUtc.ToString("O"),
            CreatedAt: nowUtc.ToString("O"),
            UpdatedAt: nowUtc.ToString("O"),
            StartedAt: null,
            EndedAt: nowUtc.ToString("O"));

    private void UpdateScheduleAfterEvaluation(GatewayAutopilotSchedule schedule, DateTimeOffset nowUtc)
    {
        TimeZoneInfo timeZone = ResolveTimeZone(schedule.TimeZoneId);
        DateTimeOffset? nextDue = AutopilotCron.GetNextOccurrence(schedule.CronExpression, timeZone, nowUtc);

        GatewayAutopilotSchedule updated = schedule with
        {
            LastEvaluatedAt = nowUtc.ToString("O"),
            NextDueAt = nextDue?.ToString("O"),
            UpdatedAt = nowUtc.ToString("O"),
            LeaseOwner = null,
            LeaseExpiresAt = null,
        };

        autopilotStore.UpdateSchedule(updated);
    }

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

    private async Task ReconcileNonTerminalRunsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<GatewayScheduledRun> runs = autopilotStore.ListNonTerminalRuns(limit: 500);
        foreach (GatewayScheduledRun run in runs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (string.Equals(run.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(run.RuntimeRunId)
                    && workflowStore.GetInstance(run.RuntimeRunId) is { } workflowInstance
                    && !WorkflowInstanceStatuses.IsTerminal(workflowInstance.Status))
                {
                    continue;
                }

                bool transitioned = autopilotStore.TransitionRunState(
                    run.Id,
                    run.Status,
                    AutopilotRunStatuses.Stale,
                    error: "Recovered on startup from non-terminal state.",
                    outputJson: null,
                    sessionId: run.SessionId,
                    runtimeRunId: run.RuntimeRunId,
                    preRunApprovalRequestId: run.PreRunApprovalRequestId,
                    nowUtc: nowUtc,
                    markStarted: false,
                    markEnded: true);

                if (transitioned
                    && string.Equals(run.Status, AutopilotRunStatuses.AwaitingPreRunApproval, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(run.PreRunApprovalRequestId))
                {
                    await ExpireApprovalAsync(run.PreRunApprovalRequestId, nowUtc, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "non-terminal run reconciliation", run.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "non-terminal run reconciliation", run.Id);
            }
        }

        IReadOnlyList<GatewayToolApprovalRequest> pendingApprovals = approvalStore.ListPending(userId: null, adminAll: true);
        foreach (GatewayToolApprovalRequest approval in pendingApprovals)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (!string.Equals(approval.ToolType, "autopilot_prerun", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(approval.RunId)
                    && autopilotStore.GetRunById(approval.RunId) is { Status: AutopilotRunStatuses.AwaitingPreRunApproval })
                {
                    continue;
                }

                await ExpireApprovalAsync(approval.Id, nowUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "orphaned approval reconciliation", approval.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutopilotSchedulerLog.RunFailed(logger, exception, "orphaned approval reconciliation", approval.Id);
            }
        }

        if (runs.Count > 0)
        {
            AutopilotSchedulerLog.ReconciledZombieRuns(logger, runs.Count);
        }

    }

    private async Task SyncWorkflowRunsAsync(CancellationToken cancellationToken)
    {
        foreach (string status in new[] { AutopilotRunStatuses.Running, AutopilotRunStatuses.WaitingForInRunApproval })
        {
            foreach (GatewayScheduledRun run in autopilotStore.ListRunsByStatus(status, 500))
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    if (!string.Equals(run.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(run.RuntimeRunId)
                        || workflowStore.GetInstance(run.RuntimeRunId) is not { } instance)
                    {
                        continue;
                    }

                    string? nextStatus = instance.Status switch
                    {
                        WorkflowInstanceStatuses.Running => AutopilotRunStatuses.Running,
                        WorkflowInstanceStatuses.Paused => AutopilotRunStatuses.WaitingForInRunApproval,
                        WorkflowInstanceStatuses.Finished => AutopilotWorkflowOutput.Serialize(instance.Envelope["output"]) is null
                            ? AutopilotRunStatuses.NoOp : AutopilotRunStatuses.Completed,
                        WorkflowInstanceStatuses.Error or WorkflowInstanceStatuses.Cancelled => AutopilotRunStatuses.Failed,
                        _ => null,
                    };
                    if (nextStatus is null || string.Equals(nextStatus, run.Status, StringComparison.Ordinal)) continue;

                    string? outputJson = instance.Status == WorkflowInstanceStatuses.Finished
                        ? AutopilotWorkflowOutput.Serialize(instance.Envelope["output"])
                        : null;
                    string? error = instance.Status is WorkflowInstanceStatuses.Error or WorkflowInstanceStatuses.Cancelled
                        ? instance.Error ?? $"Workflow instance {instance.Status}."
                        : null;
                    bool terminal = AutopilotRunStatuses.IsTerminal(nextStatus);
                    bool transitioned = autopilotStore.TransitionRunState(
                        run.Id, run.Status, nextStatus, error, outputJson,
                        run.SessionId, run.RuntimeRunId, run.PreRunApprovalRequestId,
                        DateTimeOffset.UtcNow, false, terminal);
                    if (transitioned && terminal)
                    {
                        string message = nextStatus switch
                        {
                            AutopilotRunStatuses.Completed => "Auto-Pilot workflow completed.",
                            AutopilotRunStatuses.NoOp => "Auto-Pilot workflow completed with no output.",
                            _ => $"Auto-Pilot workflow failed: {error}",
                        };
                        await notificationDispatcher.DispatchNotificationsAsync(run, run.ScheduleId, null, message, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    AutopilotSchedulerLog.RunFailed(logger, exception, "workflow run synchronization", run.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AutopilotSchedulerLog.RunFailed(logger, exception, "workflow run synchronization", run.Id);
                }
            }
        }
    }

    private bool TryGetActiveOwner(string ownerUserId, out GatewayUser? user)
    {
        user = null;
        return userStore.TryGetUserById(ownerUserId, out user)
            && user is not null
            && user.IsActive;
    }

    private bool TryGetRunOwner(GatewayScheduledRun run, out GatewayUser? user)
        => TryGetActiveOwner(run.OwnerUserId, out user);

    private async ValueTask ExpireApprovalAsync(string requestId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (approvalResolutionService is not null)
        {
            _ = await approvalResolutionService.ExpireAsync(requestId, nowUtc, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = approvalStore.Expire(requestId, nowUtc, "Approval expired.");
    }
}
