using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.RuntimePlane.Projection;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IWorkflowCommandService
{
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

/// <summary>
/// Durable workflow commands enqueue continuation work or request run stop.
/// They never publish execution-start lifecycle events; the dispatcher/runtime owns RunResumed/ToolCallStarted.
/// </summary>
internal sealed class WorkflowCommandService(
    IRuntimeEventHistoryReader historyReader,
    IRunStopCommandService runStopCommandService,
    IRunWaitStateStore waitStateStore,
    IGatewayApprovalStore approvalStore,
    IApprovalContinuationQueue continuationQueue,
    IAgentToolInvocationLinkStore invocationLinks,
    IAgentDefinitionStore agentDefinitionStore,
    IAgentToolApprovalWaiter? agentToolApprovalWaiter = null) : IWorkflowCommandService
{
    public async ValueTask<ThreadCommandResult> ResumeWaitingRootAsync(
        ResumeWaitingRootCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NaradPersistedEvent> sessionHistory = historyReader.ReadSessionHistory(command.AgentId, command.ThreadId);
        string? rootRunId = command.RunId
            ?? ExecutionGraphCommandResolver.FindLatestWaitingRootRunId(sessionHistory, command.ThreadId);
        if (string.IsNullOrWhiteSpace(rootRunId))
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "No waiting root run for this thread.");
        }

        if (!IsRootPaused(sessionHistory, rootRunId))
        {
            return new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: "Root run is not in a waiting state.");
        }

        RunWaitState? activeWait = FindActiveWaitForRoot(rootRunId);
        if (activeWait is null)
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "No recoverable wait boundary exists for explicit resume; attach to the waiting root instead.");
        }

        return await EnqueueContinuationForWaitAsync(
            activeWait,
            command.UserId,
            command.ResolvedApprovalRequestId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ThreadCommandResult> RetryTargetAsync(
        RetryExecutionTargetCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(command.TargetType, "approval", StringComparison.Ordinal))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "Only approval continuation retry is supported; child/tool/internal graph nodes cannot be retried.");
        }

        GatewayToolApprovalRequest? approval = approvalStore.GetById(command.TargetId);
        if (approval is null)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request not found.");
        }

        if (string.IsNullOrWhiteSpace(approval.Decision))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.Conflict,
                Detail: "Resolve the approval before retrying continuation dispatch.");
        }

        RunWaitState? wait = waitStateStore.GetActiveByCorrelationId(approval.Id)
            ?? waitStateStore.ListRecoverable()
                .FirstOrDefault(item => string.Equals(item.ApprovalRequestId, approval.Id, StringComparison.Ordinal));
        if (wait is null)
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "No recoverable wait boundary exists for this approval continuation retry.");
        }

        waitStateStore.ReleaseForRetry(wait.WaitId, $"thread-command:{command.UserId}");
        return await EnqueueContinuationAsync(approval, command.UserId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ThreadCommandResult> CancelGraphNodeAsync(
        CancelGraphNodeCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NaradPersistedEvent> sessionHistory = historyReader.ReadSessionHistory(command.AgentId, command.ThreadId);
        string? rootRunId = sessionHistory
            .Select(item => item.RootRunId)
            .LastOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(rootRunId))
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "No root execution for this thread.");
        }

        NaradGraphProjectionState graph = ExecutionGraphCommandResolver.BuildGraph(rootRunId, sessionHistory);
        ExecutionGraphCommandNode? resolved = ExecutionGraphCommandResolver.TryResolveNode(graph, command.NodeId, rootRunId);
        if (resolved is null)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Graph node not found.");
        }

        if (!resolved.CanCancel)
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: BuildUnsupportedCancelDetail(resolved.Node));
        }

        if (resolved.Node.Kind != "run")
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: BuildUnsupportedCancelDetail(resolved.Node));
        }

        string runId = resolved.Node.NodeId.StartsWith("node-run-", StringComparison.Ordinal)
            ? resolved.Node.NodeId["node-run-".Length..]
            : rootRunId;

        return await CancelRunAsync(command, runId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ThreadCommandResult> CancelRunAsync(
        CancelGraphNodeCommand command,
        string runId,
        CancellationToken cancellationToken)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(command.AgentId);
        string admissionRootRunId = agentDefinitionStore.FindNaradRootRunIdForRun(storageLocatorKey, runId) ?? runId;
        if (agentDefinitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, admissionRootRunId))
        {
            return new ThreadCommandResult(ThreadCommandStatus.Conflict, Detail: "Run is already terminal.");
        }

        RunStopCommandStatus stopStatus = await runStopCommandService.RequestStopAsync(
            new RunStopCommand(
                command.AgentId,
                command.ThreadId,
                runId,
                command.UserId,
                command.UserId,
                "thread_command"),
            cancellationToken).ConfigureAwait(false);

        return stopStatus switch
        {
            RunStopCommandStatus.Accepted => new ThreadCommandResult(ThreadCommandStatus.Accepted, Decision: "cancelled"),
            RunStopCommandStatus.AlreadyRequested => new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: "cancelled",
                Detail: "Run stop already requested."),
            RunStopCommandStatus.AlreadyTerminal => new ThreadCommandResult(
                ThreadCommandStatus.Conflict,
                Detail: "Run is already terminal."),
            _ => new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Run not found."),
        };
    }

    private async ValueTask<ThreadCommandResult> EnqueueContinuationForWaitAsync(
        RunWaitState wait,
        string userId,
        string? resolvedApprovalRequestId,
        CancellationToken cancellationToken)
    {
        string? approvalRequestId = wait.WaitReason switch
        {
            RunWaitReasons.Approval
                when !string.IsNullOrWhiteSpace(resolvedApprovalRequestId)
                    && PackedToolApproval.WaitCoversRequest(wait, resolvedApprovalRequestId)
                => resolvedApprovalRequestId,
            RunWaitReasons.Approval => wait.CorrelationId,
            RunWaitReasons.Child when !string.IsNullOrWhiteSpace(wait.ChildSessionId)
                => invocationLinks.GetByChildSessionId(wait.ChildSessionId)?.ApprovalRequestId,
            _ => wait.ApprovalRequestId,
        };

        if (string.IsNullOrWhiteSpace(approvalRequestId))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "No approval continuation path exists for this wait boundary.");
        }

        GatewayToolApprovalRequest? approval = approvalStore.GetById(approvalRequestId);
        if (approval is null)
        {
            return new ThreadCommandResult(ThreadCommandStatus.NotFound, Detail: "Approval request not found for wait boundary.");
        }

        if (string.IsNullOrWhiteSpace(approval.Decision))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.Conflict,
                Detail: "Resolve the pending approval before resuming the waiting root.");
        }

        return await EnqueueContinuationAsync(approval, userId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ThreadCommandResult> EnqueueContinuationAsync(
        GatewayToolApprovalRequest approval,
        string userId,
        CancellationToken cancellationToken)
    {
        if (agentToolApprovalWaiter?.IsWaiting(approval.Id) == true)
        {
            // Nested AAAT owns this approval in-process; durable enqueue would race the waiter.
            return new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: "awaited_in_process",
                Detail: "Approval is being awaited by an in-process agent-tool invocation.");
        }

        AgentToolInvocationLink? nestedLink = invocationLinks.GetByApprovalRequestId(approval.Id);
        if (nestedLink is not null
            && !string.IsNullOrWhiteSpace(nestedLink.CallerSessionId)
            && !string.IsNullOrWhiteSpace(nestedLink.CallerToolCallId)
            && waitStateStore.GetActiveChildPauseForCaller(nestedLink.CallerSessionId, nestedLink.CallerToolCallId) is null)
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.AlreadyApplied,
                Decision: "awaited_in_process",
                Detail: "Nested AAAT approval is owned by an in-process agent-tool awaiter.");
        }

        if (string.IsNullOrWhiteSpace(approval.SessionId) || string.IsNullOrWhiteSpace(approval.AgentId))
        {
            return new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "Approval record lacks continuation routing metadata.");
        }

        bool approved = string.Equals(approval.Decision, "approved", StringComparison.Ordinal);
        ApprovalContinuationEnqueueResult enqueueResult = await continuationQueue.EnqueueAsync(
            new ApprovalContinuationWork(
                approval.Id,
                approval.SessionId,
                "agent",
                approval.AgentId,
                userId,
                approved,
                HitlResolutionSource.Browser,
                approval.Reason,
                userId,
                StopRun: string.Equals(approval.Decision, "stopped", StringComparison.Ordinal)),
            cancellationToken).ConfigureAwait(false);

        return enqueueResult switch
        {
            ApprovalContinuationEnqueueResult.Inserted
                or ApprovalContinuationEnqueueResult.Rescheduled
                => new ThreadCommandResult(ThreadCommandStatus.Accepted, Decision: "continuation_enqueued"),
            ApprovalContinuationEnqueueResult.AlreadyPending
                or ApprovalContinuationEnqueueResult.AlreadyLeased
                => new ThreadCommandResult(
                    ThreadCommandStatus.AlreadyApplied,
                    Decision: "continuation_enqueued",
                    Detail: "Continuation is already scheduled."),
            _ => new ThreadCommandResult(
                ThreadCommandStatus.UnsupportedCapability,
                Detail: "No continuation work could be scheduled for this approval."),
        };
    }

    private RunWaitState? FindActiveWaitForRoot(string rootRunId)
        => waitStateStore.ListRecoverable()
            .Where(wait => string.Equals(wait.RootRunId, rootRunId, StringComparison.Ordinal)
                || string.Equals(wait.ContinuationRunId, rootRunId, StringComparison.Ordinal)
                || string.Equals(wait.RunId, rootRunId, StringComparison.Ordinal))
            .OrderByDescending(wait => wait.PausedAt, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsRootPaused(IReadOnlyList<NaradPersistedEvent> sessionHistory, string rootRunId)
    {
        NaradPersistedEvent? latestLifecycle = sessionHistory
            .Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal)
                && string.Equals(item.RunId, rootRunId, StringComparison.Ordinal)
                && NaradRunLifecycle.IsLifecycleEvent(item.EventType))
            .OrderBy(static item => item.SessionSequence)
            .LastOrDefault();
        return latestLifecycle?.EventType is NaradEventTypes.RunPaused;
    }

    private static string BuildUnsupportedCancelDetail(NaradGraphNode node)
        => node.Kind switch
        {
            "tool" => "Individual tool cancellation is unsupported; cancel the nested child run or stop the root instead.",
            "interrupt" => "Cancellation is not supported for interrupt nodes; resolve or stop the root instead.",
            _ when node.Status is "completed" or "failed" or "cancelled"
                => "Graph node is already terminal.",
            _ => "This graph node does not expose a cancellable run boundary.",
        };
}
