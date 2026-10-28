using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

namespace Kautuka.Gateway.Domain.Execution;

internal enum ThreadCommandStatus
{
    Accepted,
    AlreadyApplied,
    NotFound,
    Forbidden,
    Expired,
    Conflict,
    UnsupportedCapability,
    Rejected,
}

internal sealed record ThreadCommandResult(
    ThreadCommandStatus Status,
    string? Decision = null,
    string? RequestId = null,
    int ResolvedCount = 0,
    string? Detail = null);

internal sealed record ResolveThreadInterruptCommand(
    string InterruptId,
    string Decision,
    string? Reason,
    string UserId,
    string Actor,
    long? ExpectedRevision,
    HitlResolutionSource Source = HitlResolutionSource.Browser,
    bool AdminFallback = false,
    string? OriginatingChannelType = null);

internal sealed record ResolveAllThreadInterruptsCommand(
    string AgentId,
    string ThreadId,
    string? RunId,
    bool Approved,
    bool TrustThisRun,
    bool StopRun,
    string? Reason,
    string UserId,
    string Actor,
    long? ExpectedRevision,
    HitlResolutionSource Source = HitlResolutionSource.Browser,
    bool AdminFallback = false);

internal sealed record StopActiveRootCommand(
    string AgentId,
    string ThreadId,
    string? RunId,
    string? Reason,
    string UserId,
    string Actor,
    long? ExpectedRevision,
    bool AdminFallback = false);

internal sealed record ResumeWaitingRootCommand(
    string AgentId,
    string ThreadId,
    string? RunId,
    string UserId,
    long? ExpectedRevision,
    bool AdminFallback = false,
    string? ResolvedApprovalRequestId = null);

internal sealed record RetryExecutionTargetCommand(
    string AgentId,
    string ThreadId,
    string TargetType,
    string TargetId,
    string UserId,
    long? ExpectedRevision,
    bool AdminFallback = false);

internal sealed record CancelGraphNodeCommand(
    string AgentId,
    string ThreadId,
    string NodeId,
    string UserId,
    long? ExpectedRevision,
    bool AdminFallback = false);

internal interface IThreadCommandService
{
    ValueTask<ThreadCommandResult> ResolveInterruptAsync(
        ResolveThreadInterruptCommand command,
        CancellationToken cancellationToken);

    ValueTask<ThreadCommandResult> ResolveAllInterruptsAsync(
        ResolveAllThreadInterruptsCommand command,
        CancellationToken cancellationToken);

    ValueTask<ThreadCommandResult> StopActiveRootAsync(
        StopActiveRootCommand command,
        CancellationToken cancellationToken);

    ValueTask<ThreadCommandResult> ResumeWaitingRootAsync(
        ResumeWaitingRootCommand command,
        CancellationToken cancellationToken);

    ValueTask<ThreadCommandResult> RetryTargetAsync(
        RetryExecutionTargetCommand command,
        CancellationToken cancellationToken);

    ValueTask<ThreadCommandResult> CancelGraphNodeAsync(
        CancelGraphNodeCommand command,
        CancellationToken cancellationToken);
}

internal sealed class ThreadCommandService(
    IGatewayApprovalStore approvalStore,
    IApprovalResolutionService approvalResolutionService,
    IRunStopCommandService runStopCommandService,
    IAgentDefinitionStore agentDefinitionStore,
    IAgentToolInvocationLinkStore invocationLinks,
    IGatewaySessionStore sessions,
    IGatewayUserStore users,
    IThreadCommandCoordinator coordinator,
    IWorkflowCommandService workflowCommands,
    IAgentToolApprovalWaiter? agentToolApprovalWaiter = null,
    IRunWaitStateStore? runWaitStateStore = null,
    IGatewayWorkflowStore? workflowStore = null) : IThreadCommandService
{
    public async ValueTask<ThreadCommandResult> ResolveInterruptAsync(
        ResolveThreadInterruptCommand command,
        CancellationToken cancellationToken)
    {
        GatewayToolApprovalRequest? request = approvalStore.GetById(command.InterruptId);
        if (request is null)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request not found.");
        }

        if (!CanAccessApproval(command.UserId, command.AdminFallback, request))
        {
            return new ThreadCommandResult(ThreadCommandStatus.Forbidden, Detail: "Approval request not found or forbidden.");
        }

        if (!TryResolveCommandOwner(request, out string agentId, out string threadId))
        {
            if (!CanResolveSessionlessApproval(request))
            {
                return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request is missing owner identity.");
            }

            return await ResolveInterruptCoreAsync(command, request, cancellationToken).ConfigureAwait(false);
        }

        bool workflowOwned = !string.IsNullOrWhiteSpace(request.InstanceId);
        long? expectedRevision = command.ExpectedRevision;
        Func<long>? readRevision = null;
        if (workflowOwned)
        {
            string instanceId = request.InstanceId!;
            if (workflowStore is not null)
            {
                readRevision = () => workflowStore.ReadNaradSessionSequence(instanceId);
            }
            else
            {
                readRevision = static () => 0;
                expectedRevision = null;
            }
        }

        ThreadCommandGateResult<ThreadCommandResult> gate = await coordinator.ExecuteAsync(
            agentId,
            threadId,
            expectedRevision,
            async (_, ct) => await ResolveInterruptCoreAsync(command, request, ct).ConfigureAwait(false),
            cancellationToken,
            readRevision).ConfigureAwait(false);

        ThreadCommandResult result = gate.Status is ThreadCommandStatus.Conflict
            ? new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: gate.Detail)
            : gate.Value ?? new ThreadCommandResult(ThreadCommandStatus.NotFound);

        if (ShouldResumeAfterBrowserResolve(command, request, result)
            && agentToolApprovalWaiter?.IsWaiting(request.Id) != true
            && !IsOwnedByInProcessAgentToolAwait(request.Id))
        {
            string resumeAgentId = !string.IsNullOrWhiteSpace(request.OuterSessionId)
                && !string.IsNullOrWhiteSpace(request.AgentId)
                    ? request.AgentId!
                    : agentId;
            string resumeThreadId = !string.IsNullOrWhiteSpace(request.OuterSessionId)
                && !string.IsNullOrWhiteSpace(request.SessionId)
                    ? request.SessionId!
                    : threadId;
            await ResumeWaitingRootAsync(
                new ResumeWaitingRootCommand(
                    resumeAgentId,
                    resumeThreadId,
                    RunId: null,
                    command.UserId,
                    ExpectedRevision: null,
                    command.AdminFallback,
                    ResolvedApprovalRequestId: request.Id),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<ThreadCommandResult> ResolveAllInterruptsAsync(
        ResolveAllThreadInterruptsCommand command,
        CancellationToken cancellationToken)
    {
        ThreadCommandGateResult<ThreadCommandResult> gate = await coordinator.ExecuteAsync(
            command.AgentId,
            command.ThreadId,
            command.ExpectedRevision,
            async (_, ct) => await ResolveAllInterruptsCoreAsync(command, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return gate.Status is ThreadCommandStatus.Conflict
            ? new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: gate.Detail)
            : gate.Value ?? new ThreadCommandResult(ThreadCommandStatus.NotFound);
    }

    public async ValueTask<ThreadCommandResult> StopActiveRootAsync(
        StopActiveRootCommand command,
        CancellationToken cancellationToken)
    {
        ThreadCommandGateResult<ThreadCommandResult> gate = await coordinator.ExecuteAsync(
            command.AgentId,
            command.ThreadId,
            command.ExpectedRevision,
            async (_, ct) => await StopActiveRootCoreAsync(command, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return gate.Status is ThreadCommandStatus.Conflict
            ? new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: gate.Detail)
            : gate.Value ?? new ThreadCommandResult(ThreadCommandStatus.NotFound);
    }

    public async ValueTask<ThreadCommandResult> ResumeWaitingRootAsync(
        ResumeWaitingRootCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeThread(command.AgentId, command.ThreadId, command.UserId, command.AdminFallback, out ThreadCommandResult? forbidden))
        {
            return forbidden!;
        }

        return await ExecuteWorkflowAsync(
            command.AgentId,
            command.ThreadId,
            command.ExpectedRevision,
            (_, ct) => workflowCommands.ResumeWaitingRootAsync(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ThreadCommandResult> RetryTargetAsync(
        RetryExecutionTargetCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeThread(command.AgentId, command.ThreadId, command.UserId, command.AdminFallback, out ThreadCommandResult? forbidden))
        {
            return forbidden!;
        }

        if (string.Equals(command.TargetType, "approval", StringComparison.Ordinal))
        {
            GatewayToolApprovalRequest? approval = approvalStore.GetById(command.TargetId);
            if (approval is null || !BelongsToThread(approval, command.ThreadId))
            {
                return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request not found for this thread.");
            }

            if (!CanAccessApproval(command.UserId, command.AdminFallback, approval))
            {
                return new ThreadCommandResult(ThreadCommandStatus.Forbidden, Detail: "Approval request not found or forbidden.");
            }
        }

        return await ExecuteWorkflowAsync(
            command.AgentId,
            command.ThreadId,
            command.ExpectedRevision,
            (_, ct) => workflowCommands.RetryTargetAsync(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ThreadCommandResult> CancelGraphNodeAsync(
        CancelGraphNodeCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeThread(command.AgentId, command.ThreadId, command.UserId, command.AdminFallback, out ThreadCommandResult? forbidden))
        {
            return forbidden!;
        }

        return await ExecuteWorkflowAsync(
            command.AgentId,
            command.ThreadId,
            command.ExpectedRevision,
            (_, ct) => workflowCommands.CancelGraphNodeAsync(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ThreadCommandResult> ExecuteWorkflowAsync(
        string agentId,
        string threadId,
        long? expectedRevision,
        Func<long, CancellationToken, ValueTask<ThreadCommandResult>> action,
        CancellationToken cancellationToken)
    {
        ThreadCommandGateResult<ThreadCommandResult> gate = await coordinator.ExecuteAsync(
            agentId,
            threadId,
            expectedRevision,
            action,
            cancellationToken).ConfigureAwait(false);

        return gate.Status is ThreadCommandStatus.Conflict
            ? new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: gate.Detail)
            : gate.Value ?? new ThreadCommandResult(ThreadCommandStatus.NotFound);
    }


    private async ValueTask<ThreadCommandResult> ResolveInterruptCoreAsync(
        ResolveThreadInterruptCommand command,
        GatewayToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApprovalActionCatalog.TryMapDecision(command.Decision, out bool approved, out bool stopRun, out string action))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.Rejected,
                Detail: "Field 'decision' is not a known interrupt action.");
        }

        string? reason = command.Reason;
        if (stopRun)
        {
            approved = false;
            reason ??= "stopped";
        }

        GatewayToolApprovalRequest? latest = approvalStore.GetById(request.Id);
        if (latest is null)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request not found.");
        }

        if (!string.IsNullOrWhiteSpace(latest.Decision))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: latest.Decision,
                RequestId: latest.Id);
        }

        if (IsWorkflowReview(latest)
            && action is ApprovalActionCatalog.RequestChanges or ApprovalActionCatalog.Deny
            && !WorkflowReviewAllows(latest, action))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.Rejected,
                RequestId: latest.Id,
                Detail: WorkflowFailureCodes.GateChangesExhaustedMessage);
        }

        ApprovalResolutionResult resolution = await approvalResolutionService.ResolveAsync(
            new ApprovalResolutionCommand(
                latest.Id,
                approved,
                reason,
                command.UserId,
                command.Source,
                latest.SessionId,
                TargetType: string.IsNullOrWhiteSpace(latest.AgentId) ? null : "agent",
                TargetId: latest.AgentId,
                UserId: command.UserId,
                StopRun: stopRun,
                TrustThisRun: false,
                OriginatingChannelType: command.OriginatingChannelType,
                DecidedByDisplayName: command.Actor,
                Action: action),
            cancellationToken).ConfigureAwait(false);

        return MapResolutionResult(resolution, latest.Id);
    }

    private static bool IsWorkflowReview(GatewayToolApprovalRequest request)
        => string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal);

    private bool WorkflowReviewAllows(GatewayToolApprovalRequest request, string action)
    {
        if (action == ApprovalActionCatalog.Deny)
        {
            return false;
        }

        if (workflowStore is null)
        {
            return false;
        }

        GatewayWorkflowInstance? instance = workflowStore.GetInstance(request.InstanceId ?? string.Empty);
        int finishedAttempts = instance?.Steps.Count(step =>
            string.Equals(step.NodeId, request.NodeId, StringComparison.Ordinal)
            && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal)) ?? 0;
        return action != ApprovalActionCatalog.RequestChanges || WorkflowRetryPolicy.CanRequestChanges(finishedAttempts);
    }

    private async ValueTask<ThreadCommandResult> ResolveAllInterruptsCoreAsync(
        ResolveAllThreadInterruptsCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeThreadForSource(command.AgentId, command.ThreadId, command.UserId, command.AdminFallback, command.Source, out ThreadCommandResult? forbidden))
        {
            return forbidden!;
        }

        int resolvedCount = 0;
        ThreadCommandResult? lastResult = null;

        while (true)
        {
            GatewayToolApprovalRequest[] pending = ListPendingForThread(command.ThreadId, command.RunId)
                .Where(request => CanAccessApproval(command, request))
                .ToArray();
            if (pending.Length == 0)
            {
                break;
            }

            int resolvedAtStart = resolvedCount;

            foreach (GatewayToolApprovalRequest request in pending)
            {
                ThreadCommandResult result = await ResolveInterruptCoreAsync(
                    new ResolveThreadInterruptCommand(
                        request.Id,
                        ApprovalActionCatalog.Resolve(
                            command.Approved,
                            expired: false,
                            command.Reason,
                            command.StopRun),
                        command.Reason,
                        command.UserId,
                        command.Actor,
                        ExpectedRevision: null,
                        command.Source,
                        command.AdminFallback),
                    request,
                    cancellationToken).ConfigureAwait(false);

                if (result.Status is ThreadCommandStatus.Accepted or ThreadCommandStatus.AlreadyApplied)
                {
                    resolvedCount++;
                    lastResult = result;
                    continue;
                }

                if (result.Status is ThreadCommandStatus.Expired or ThreadCommandStatus.Conflict or ThreadCommandStatus.Forbidden)
                {
                    return result with { ResolvedCount = resolvedCount };
                }

                if (result.Status is ThreadCommandStatus.NotFound)
                {
                    return new ThreadCommandResult(
                        ThreadCommandStatus.NotFound,
                        RequestId: request.Id,
                        ResolvedCount: resolvedCount,
                        Detail: result.Detail ?? "Approval request could not be resolved.");
                }
            }

            if (resolvedCount == resolvedAtStart)
            {
                break;
            }
        }

        if (resolvedCount == 0 && HasApprovalScopeHistory(command))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: lastResult?.Decision,
                RequestId: lastResult?.RequestId,
                ResolvedCount: 0,
                Detail: "No pending approvals for this thread.");
        }

        return new ThreadCommandResult(
            resolvedCount > 0 ? ThreadCommandStatus.Accepted : ThreadCommandStatus.NotFound,
            Decision: lastResult?.Decision,
            RequestId: lastResult?.RequestId,
            ResolvedCount: resolvedCount,
            Detail: resolvedCount == 0 ? "No pending approvals for this thread." : null);
    }

    private async ValueTask<ThreadCommandResult> StopActiveRootCoreAsync(
        StopActiveRootCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeThread(command.AgentId, command.ThreadId, command.UserId, command.AdminFallback, out ThreadCommandResult? forbidden))
        {
            return forbidden!;
        }

        string? runId = command.RunId ?? TryFindLatestLiveRootRunId(command.AgentId, command.ThreadId);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "No active root run for this thread.");
        }

        RunStopCommandStatus stopStatus = await runStopCommandService.RequestStopAsync(
            new RunStopCommand(
                command.AgentId,
                command.ThreadId,
                runId,
                command.UserId,
                command.Actor,
                "chat"),
            cancellationToken).ConfigureAwait(false);

        if (stopStatus is RunStopCommandStatus.NotFound)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Root run not found.");
        }

        if (stopStatus is RunStopCommandStatus.AlreadyTerminal)
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.Conflict,
                Decision: "stopped",
                Detail: "Root run is already terminal.");
        }

        ThreadCommandResult settle = await ResolveAllInterruptsCoreAsync(
            new ResolveAllThreadInterruptsCommand(
                command.AgentId,
                command.ThreadId,
                runId,
                Approved: false,
                TrustThisRun: false,
                StopRun: false,
                command.Reason ?? "stopped",
                command.UserId,
                command.Actor,
                ExpectedRevision: null,
                AdminFallback: command.AdminFallback),
            cancellationToken).ConfigureAwait(false);

        return stopStatus switch
        {
            RunStopCommandStatus.Accepted => new ThreadCommandResult(
                ThreadCommandStatus.Accepted,
                Decision: "stopped",
                ResolvedCount: settle.ResolvedCount,
                Detail: settle.Detail),
            RunStopCommandStatus.AlreadyRequested when settle.ResolvedCount > 0 => new ThreadCommandResult(
                ThreadCommandStatus.Accepted,
                Decision: "stopped",
                ResolvedCount: settle.ResolvedCount,
                Detail: settle.Detail),
            RunStopCommandStatus.AlreadyRequested => new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: "stopped",
                ResolvedCount: settle.ResolvedCount,
                Detail: "Root stop already requested."),
            _ => new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: "Root run stop could not be applied."),
        };
    }

    private bool TryAuthorizeThreadForSource(
        string agentId,
        string threadId,
        string userId,
        bool adminFallback,
        HitlResolutionSource source,
        out ThreadCommandResult? forbidden)
    {
        if (source is HitlResolutionSource.ApprovalApi)
        {
            forbidden = null;
            return true;
        }

        return TryAuthorizeThread(agentId, threadId, userId, adminFallback, out forbidden);
    }

    private bool IsRootStopAlreadyPersisted(string agentId, string rootRunId)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        return agentDefinitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId);
    }

    private bool HasApprovalScopeHistory(ResolveAllThreadInterruptsCommand command)
        => ListScopedApprovals(command.ThreadId, command.RunId).Length > 0;

    private GatewayToolApprovalRequest[] ListScopedApprovals(string threadId, string? runId)
        => approvalStore
            .ListBySession(threadId)
            .Concat(approvalStore.ListByOuterSession(threadId))
            .Where(request => string.IsNullOrWhiteSpace(runId)
                || ApprovalRootRunIdResolver.BelongsToRootScope(request, runId, invocationLinks, agentDefinitionStore))
            .GroupBy(request => request.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private bool TryAuthorizeThread(
        string agentId,
        string threadId,
        string userId,
        bool adminFallback,
        out ThreadCommandResult? forbidden)
    {
        forbidden = null;
        if (!sessions.TryGetSession(threadId, out GatewaySession? session)
            || session is null
            || !string.Equals(session.TargetId, agentId, StringComparison.Ordinal))
        {
            forbidden = new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Thread not found.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return true;
        }

        if (!users.TryGetUserById(userId, out GatewayUser? user) || user is null)
        {
            forbidden = new ThreadCommandResult(ThreadCommandStatus.Forbidden, Detail: "Thread access forbidden.");
            return false;
        }

        if (GatewayOwnershipAccess.CanAccessSession(user, session, adminFallback))
        {
            return true;
        }

        forbidden = new ThreadCommandResult(ThreadCommandStatus.Forbidden, Detail: "Thread access forbidden.");
        return false;
    }

    private bool CanAccessApproval(ResolveThreadInterruptCommand command, GatewayToolApprovalRequest request)
        => CanAccessApproval(command.UserId, command.AdminFallback, request);

    private bool CanAccessApproval(ResolveAllThreadInterruptsCommand command, GatewayToolApprovalRequest request)
        => CanAccessApproval(command.UserId, command.AdminFallback, request);

    private bool CanAccessApproval(string userId, bool adminFallback, GatewayToolApprovalRequest request)
    {
        if (!users.TryGetUserById(userId, out GatewayUser? user) || user is null)
        {
            return false;
        }

        if (GatewayOwnershipAccess.CanAccessApproval(user, request, adminFallback))
        {
            return true;
        }

        foreach (string? sessionId in new[] { request.SessionId, request.OuterSessionId })
        {
            if (!string.IsNullOrWhiteSpace(sessionId)
                && sessions.TryGetSession(sessionId, out GatewaySession? session)
                && GatewayOwnershipAccess.CanAccessSession(user, session, adminFallback))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldResumeAfterBrowserResolve(
        ResolveThreadInterruptCommand command,
        GatewayToolApprovalRequest request,
        ThreadCommandResult result)
    {
        if (command.Source is not HitlResolutionSource.Browser
            || result.Status is not (ThreadCommandStatus.Accepted or ThreadCommandStatus.AlreadyApplied))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.InstanceId)
            || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal))
        {
            return false;
        }

        if (!ApprovalActionCatalog.TryMapDecision(command.Decision, out _, out bool stopRun, out _)
            || stopRun)
        {
            return false;
        }

        RunWaitState? wait = PackedToolApproval.FindActiveWait(
            runWaitStateStore,
            request.Id,
            continuationRunId: null);
        IReadOnlyList<string> packedIds = PackedToolApproval.ReadPackedRequestIds(wait, request.Id);
        return packedIds.Count <= 1
            || !PackedToolApproval.HasUndecidedHumanSiblings(approvalStore, packedIds, request.Id);
    }

    private static bool TryResolveCommandOwner(
        GatewayToolApprovalRequest request,
        out string agentId,
        out string threadId)
    {
        if (!string.IsNullOrWhiteSpace(request.InstanceId))
        {
            agentId = !string.IsNullOrWhiteSpace(request.WorkflowId) ? request.WorkflowId! : request.InstanceId;
            threadId = request.InstanceId;
            return true;
        }

        agentId = request.AgentId ?? string.Empty;
        threadId = !string.IsNullOrWhiteSpace(request.SessionId)
            ? request.SessionId
            : request.OuterSessionId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(threadId);
    }

    private static bool CanResolveSessionlessApproval(GatewayToolApprovalRequest request)
        => string.IsNullOrWhiteSpace(request.InstanceId)
            && string.IsNullOrWhiteSpace(request.SessionId)
            && string.IsNullOrWhiteSpace(request.OuterSessionId)
            && !string.IsNullOrWhiteSpace(request.AgentId);

    private bool IsOwnedByInProcessAgentToolAwait(string requestId)
    {
        if (runWaitStateStore is null)
        {
            return false;
        }

        AgentToolInvocationLink? link = invocationLinks.GetByApprovalRequestId(requestId);
        if (link is null
            || string.IsNullOrWhiteSpace(link.CallerSessionId)
            || string.IsNullOrWhiteSpace(link.CallerToolCallId))
        {
            return false;
        }

        return runWaitStateStore.GetActiveChildPauseForCaller(link.CallerSessionId, link.CallerToolCallId) is null;
    }

    private static ThreadCommandResult MapResolutionResult(
        ApprovalResolutionResult resolution,
        string requestId)
        => resolution.Status switch
        {
            ApprovalResolutionStatus.Resolved => new ThreadCommandResult(
                ThreadCommandStatus.Accepted,
                Decision: resolution.Request?.Decision,
                RequestId: requestId),
            ApprovalResolutionStatus.AlreadyResolved => new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: resolution.Request?.Decision,
                RequestId: requestId),
            ApprovalResolutionStatus.Expired => new ThreadCommandResult(
                ThreadCommandStatus.Expired,
                Decision: resolution.Request?.Decision ?? "expired",
                RequestId: requestId,
                Detail: "Approval request has expired."),
            ApprovalResolutionStatus.Rejected => new ThreadCommandResult(
                ThreadCommandStatus.Rejected,
                RequestId: requestId,
                Detail: "That action is not available for this review."),
            _ => new ThreadCommandResult(
                ThreadCommandStatus.NotFound,
                RequestId: requestId,
                Detail: "Approval request not found."),
        };

    private string? TryFindLatestLiveRootRunId(string agentId, string threadId)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        IReadOnlyList<string> rootRunIds = agentDefinitionStore.ListNaradRootRunIdsForSession(storageLocatorKey, threadId);
        for (int index = rootRunIds.Count - 1; index >= 0; index--)
        {
            string rootRunId = rootRunIds[index];
            if (!agentDefinitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId))
            {
                return rootRunId;
            }
        }

        return null;
    }

    private GatewayToolApprovalRequest[] ListPendingForThread(string threadId, string? runId)
        => ListScopedApprovals(threadId, runId)
            .Where(request => string.IsNullOrWhiteSpace(request.Decision))
            .OrderBy(request => request.RequestedAt, StringComparer.Ordinal)
            .ToArray();

    private bool BelongsToThread(GatewayToolApprovalRequest request, string threadId)
    {
        if (string.Equals(request.SessionId, threadId, StringComparison.Ordinal)
            || string.Equals(request.OuterSessionId, threadId, StringComparison.Ordinal))
        {
            return true;
        }

        AgentToolInvocationLink? link = invocationLinks.GetByApprovalRequestId(request.Id);
        return string.Equals(link?.CallerSessionId, threadId, StringComparison.Ordinal);
    }
}
