using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Hitl;

internal enum ApprovalResolutionStatus
{
    Resolved = 0,
    NotFound = 1,
    AlreadyResolved = 2,
    Expired = 3,
    Rejected = 4,
}

internal sealed record ApprovalResolutionCommand(
    string RequestId,
    bool Approved,
    string? Reason,
    string? DecidedByUserId,
    HitlResolutionSource Source,
    string? SessionId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? UserId = null,
    bool StopRun = false,
    bool TrustThisRun = false,
    string? OriginatingChannelType = null,
    string? DecidedByDisplayName = null,
    string? Action = null);

internal sealed record ApprovalResolutionResult(
    ApprovalResolutionStatus Status,
    GatewayToolApprovalRequest? Request);

internal interface IApprovalResolutionService
{
    ValueTask<ApprovalResolutionResult> ResolveAsync(ApprovalResolutionCommand command, CancellationToken cancellationToken);

    ValueTask<ApprovalResolutionResult> ExpireAsync(string requestId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

internal sealed class ApprovalResolutionService(
    IGatewayApprovalStore approvalStore,
    IMediator mediator,
    IRunStopRegistry runStopRegistry,
    IApprovalResolutionEventDispatcher resolutionEventDispatcher,
    IAgentToolInvocationLinkStore invocationLinkStore,
    IAgentDefinitionStore agentDefinitionStore,
    ILogger<ApprovalResolutionService> logger,
    IGatewayAutopilotStore? autopilotStore = null,
    IAgentToolApprovalWaiter? agentToolApprovalWaiter = null,
    IRunLimitResolver? runLimitResolver = null,
    IRunWaitStateStore? runWaitStateStore = null,
    IGatewayWorkflowStore? workflowStore = null) : IApprovalResolutionService
{
    public async ValueTask<ApprovalResolutionResult> ResolveAsync(ApprovalResolutionCommand command, CancellationToken cancellationToken)
    {
        ApprovalResolutionServiceLog.ResolveRequested(
            logger,
            command.RequestId,
            command.Approved,
            command.Source,
            command.StopRun,
            command.SessionId);

        GatewayToolApprovalRequest? request = approvalStore.GetById(command.RequestId);
        if (request is null)
        {
            ApprovalResolutionServiceLog.ResolveNotFound(logger, command.RequestId);
            return new ApprovalResolutionResult(ApprovalResolutionStatus.NotFound, null);
        }

        if (!string.IsNullOrWhiteSpace(request.Decision))
        {
            ApprovalResolutionServiceLog.ResolveAlreadyResolved(logger, command.RequestId, request.Decision);
            return new ApprovalResolutionResult(ApprovalResolutionStatus.AlreadyResolved, request);
        }

        if (string.Equals(request.ToolType, "autopilot_prerun", StringComparison.Ordinal)
            && autopilotStore is not null
            && (string.IsNullOrWhiteSpace(request.RunId)
                || autopilotStore.GetRunById(request.RunId) is not { Status: AutopilotRunStatuses.AwaitingPreRunApproval }))
        {
            return await ExpireAsync(command.RequestId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        if (DateTimeOffset.TryParse(request.ExpiresAt, out DateTimeOffset expiresAt) && expiresAt <= DateTimeOffset.UtcNow)
        {
            return await ExpireAsync(command.RequestId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        string action = string.IsNullOrWhiteSpace(command.Action)
            ? ApprovalActionCatalog.Resolve(
                command.Approved,
                expired: false,
                command.Reason,
                command.StopRun)
            : command.Action;
        bool isWorkflowReview = string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal);
        if (isWorkflowReview)
        {
            GatewayWorkflowInstance? instance = workflowStore?.GetInstance(request.InstanceId ?? string.Empty);
            int finishedAttempts = instance?.Steps.Count(step =>
                string.Equals(step.NodeId, request.NodeId, StringComparison.Ordinal)
                && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal)) ?? 0;
            bool actionAllowed = ApprovalActionCatalog.ForToolType("workflow_review").Any(candidate =>
                string.Equals(candidate.Value, action, StringComparison.Ordinal)
                && (instance is null
                    ? candidate.Value == ApprovalActionCatalog.StopRun
                    : candidate.Value != ApprovalActionCatalog.RequestChanges
                        || WorkflowRetryPolicy.CanRequestChanges(finishedAttempts)));
            if (!actionAllowed)
            {
                return new ApprovalResolutionResult(ApprovalResolutionStatus.Rejected, request);
            }
        }
        string? resolutionReason = isWorkflowReview
            && !command.Approved
            && !command.StopRun
            && string.IsNullOrWhiteSpace(command.Reason)
                ? ApprovalActionCatalog.RequestChanges
                : command.Reason;
        command = command with { Reason = resolutionReason };

        string resolutionSource = ToStorageSource(command.Source, command.OriginatingChannelType);
        AgentToolInvocationLink? invocationLink = invocationLinkStore.GetByApprovalRequestId(request.Id);
        NaradEventContext? runtimeEventContext = BuildRuntimeEventContext(request, command.SessionId, command.UserId, invocationLink);
        (string? targetType, string? targetId) = ResolveTarget(request, command);
        string? sessionId = ResolveRuntimeSessionId(request, command.SessionId, invocationLink);
        string? userId = string.IsNullOrWhiteSpace(command.UserId) ? request.UserId : command.UserId;
        ApprovalContinuationWork? continuation = command.Source != HitlResolutionSource.Browser
            && agentToolApprovalWaiter?.IsWaiting(command.RequestId) != true
            && !string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)
            && !string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(sessionId)
            && !string.IsNullOrWhiteSpace(targetType)
            && !string.IsNullOrWhiteSpace(targetId)
                ? new ApprovalContinuationWork(
                    command.RequestId,
                    sessionId,
                    targetType,
                    targetId,
                    userId,
                    command.Approved,
                    command.Source,
                    command.Reason,
                    command.DecidedByUserId,
                    command.StopRun)
                : null;
        ApprovalResolutionEventWork? resolutionEvent = TryBuildResolutionEvent(
            request,
            command.Approved,
            command.Reason,
            command.DecidedByDisplayName,
            resolutionSource,
            command.StopRun,
            runtimeEventContext,
            invocationLink?.CallerSessionId ?? request.OuterSessionId ?? sessionId);

        string? bypassRootRunId = ApprovalRootRunIdResolver.Resolve(request, invocationLinkStore, agentDefinitionStore);

        string storageDecision = ApprovalActionCatalog.StorageDecision(action);
        bool resolved = approvalStore.Resolve(
            command.RequestId,
            storageDecision,
            command.Reason,
            command.DecidedByUserId,
            resolutionSource,
            continuation,
            resolutionEvent);
        if (!resolved)
        {
            GatewayToolApprovalRequest? latest = approvalStore.GetById(command.RequestId);
            if (latest is not null
                && latest.Decision is null
                && DateTimeOffset.TryParse(latest.ExpiresAt, out DateTimeOffset latestExpiresAt)
                && latestExpiresAt <= DateTimeOffset.UtcNow)
            {
                return await ExpireAsync(command.RequestId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            ApprovalResolutionServiceLog.ResolveStoreRace(logger, command.RequestId, latest?.Decision);
            return new ApprovalResolutionResult(
                latest is null
                    ? ApprovalResolutionStatus.NotFound
                    : !string.IsNullOrWhiteSpace(latest.Decision)
                        ? string.Equals(latest.Decision, "expired", StringComparison.Ordinal)
                            ? ApprovalResolutionStatus.Expired
                            : ApprovalResolutionStatus.AlreadyResolved
                        : ApprovalResolutionStatus.NotFound,
                latest);
        }

        CancellationToken committedResolutionToken = CancellationToken.None;

        if (command.StopRun && !string.IsNullOrWhiteSpace(bypassRootRunId))
        {
            runStopRegistry.RegisterStop(bypassRootRunId);
        }

        await resolutionEventDispatcher.DispatchAsync(
            resolutionEvent?.RequestId ?? command.RequestId,
            committedResolutionToken).ConfigureAwait(false);

        await mediator.Publish(new HitlApprovalResolved(
            command.RequestId,
            command.Approved,
            command.Reason,
            command.DecidedByUserId,
            command.Source,
            sessionId,
            targetType,
            targetId,
            userId,
            StopRun: command.StopRun), committedResolutionToken).ConfigureAwait(false);

        ApprovalResolutionServiceLog.ResolveCompleted(
            logger,
            command.RequestId,
            command.Approved,
            sessionId,
            targetType,
            targetId,
            userId);

        return new ApprovalResolutionResult(
            ApprovalResolutionStatus.Resolved,
            approvalStore.GetById(command.RequestId) ?? request);
    }

    private static string ToStorageSource(HitlResolutionSource source, string? originatingChannelType = null) =>
        source switch
        {
            HitlResolutionSource.Browser => "chat",
            HitlResolutionSource.ApprovalApi => "approvals",
            HitlResolutionSource.ChannelCallback when !string.IsNullOrWhiteSpace(originatingChannelType) => originatingChannelType!,
            HitlResolutionSource.ChannelCallback => throw new InvalidOperationException(
                "Channel callback approval resolutions require an originating channel type."),
            HitlResolutionSource.SystemTimeout => "system_timeout",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };

    public async ValueTask<ApprovalResolutionResult> ExpireAsync(string requestId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        GatewayToolApprovalRequest? request = approvalStore.GetById(requestId);
        if (request is null)
        {
            return new ApprovalResolutionResult(ApprovalResolutionStatus.NotFound, null);
        }

        if (!string.IsNullOrWhiteSpace(request.Decision))
        {
            return new ApprovalResolutionResult(
                string.Equals(request.Decision, "expired", StringComparison.Ordinal)
                    ? ApprovalResolutionStatus.Expired
                    : ApprovalResolutionStatus.AlreadyResolved,
                request);
        }

        ResolvedRunLimits limits = runLimitResolver?.ResolveForAgent(request.AgentId)
            ?? new ResolvedRunLimits(TimeSpan.FromSeconds(300), null, null, HitlExpiryAction.Abort);
        HitlExpiryAction action = string.Equals(request.ToolType, "autopilot_prerun", StringComparison.Ordinal)
            ? HitlExpiryAction.Inform
            : limits.ExpiryAction;
        if (action == HitlExpiryAction.Retry)
        {
            TimeSpan retryWindow = limits.HitlExpiry ?? TimeSpan.Zero;
            if (retryWindow > TimeSpan.Zero
                && !string.Equals(request.Reason, HitlExpiryReasons.RetryMarker, StringComparison.Ordinal)
                && approvalStore.TryScheduleHitlRetry(requestId, nowUtc.Add(retryWindow), nowUtc))
            {
                return new ApprovalResolutionResult(ApprovalResolutionStatus.Expired, approvalStore.GetById(requestId) ?? request);
            }

            action = HitlExpiryAction.Abort;
        }

        bool abort = action != HitlExpiryAction.Inform;
        string expirationReason = abort ? HitlExpiryReasons.Abort : HitlExpiryReasons.Inform;
        NaradEventContext? runtimeEventContext = BuildRuntimeEventContext(request, request.SessionId, request.UserId);
        bool waiterOwns = agentToolApprovalWaiter?.IsWaiting(requestId) == true;
        ApprovalContinuationWork? continuation = !waiterOwns
            && !string.IsNullOrWhiteSpace(request.SessionId)
            && !string.IsNullOrWhiteSpace(request.AgentId)
                ? new ApprovalContinuationWork(
                    requestId,
                    request.SessionId,
                    "agent",
                    request.AgentId,
                    request.UserId,
                    Approved: false,
                    HitlResolutionSource.SystemTimeout,
                    expirationReason,
                    DecidedByUserId: null,
                    StopRun: abort)
                : null;
        string expirationSource = "system_timeout";
        ApprovalResolutionEventWork? resolutionEvent = TryBuildResolutionEvent(
            request,
            approved: false,
            reason: expirationReason,
            decidedByDisplayName: null,
            resolutionSource: expirationSource,
            stopRun: abort,
            context: runtimeEventContext,
            stopThreadId: request.OuterSessionId ?? request.SessionId);

        if (abort && !string.IsNullOrWhiteSpace(runtimeEventContext?.RootRunId))
        {
            runStopRegistry.RegisterStop(runtimeEventContext.RootRunId);
        }

        if (!approvalStore.Expire(requestId, nowUtc, expirationReason, continuation, resolutionEvent))
        {
            GatewayToolApprovalRequest? latest = approvalStore.GetById(requestId);
            return new ApprovalResolutionResult(
                string.Equals(latest?.Decision, "expired", StringComparison.Ordinal)
                    ? ApprovalResolutionStatus.Expired
                    : ApprovalResolutionStatus.AlreadyResolved,
                latest);
        }

        CancellationToken committedResolutionToken = CancellationToken.None;
        await resolutionEventDispatcher.DispatchAsync(
            resolutionEvent?.RequestId ?? requestId,
            committedResolutionToken).ConfigureAwait(false);

        await mediator.Publish(new HitlApprovalResolved(
            requestId,
            Approved: false,
            Reason: expirationReason,
            DecidedByUserId: null,
            Source: HitlResolutionSource.SystemTimeout,
            SessionId: request.SessionId,
            TargetType: string.IsNullOrWhiteSpace(request.AgentId) ? null : "agent",
            TargetId: request.AgentId,
            UserId: request.UserId,
            StopRun: abort), committedResolutionToken).ConfigureAwait(false);

        return new ApprovalResolutionResult(ApprovalResolutionStatus.Expired, approvalStore.GetById(requestId) ?? request);
    }

    private static (string? TargetType, string? TargetId) ResolveTarget(
        GatewayToolApprovalRequest request,
        ApprovalResolutionCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.TargetType) && !string.IsNullOrWhiteSpace(command.TargetId))
        {
            return (command.TargetType, command.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            return ("agent", request.AgentId);
        }

        return (command.TargetType, command.TargetId);
    }

    private NaradEventContext? BuildRuntimeEventContext(
        GatewayToolApprovalRequest request,
        string? sessionIdOverride,
        string? userIdOverride,
        AgentToolInvocationLink? invocationLink = null)
    {
        if (string.Equals(request.ToolType, "autopilot_prerun", StringComparison.Ordinal)
            || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal))
        {
            return null;
        }

        invocationLink ??= invocationLinkStore.GetByApprovalRequestId(request.Id);
        if (!string.IsNullOrWhiteSpace(request.OuterSessionId) && invocationLink is null)
        {
            ApprovalResolutionServiceLog.MissingInvocationLineage(
                logger,
                request.Id,
                request.OuterSessionId,
                request.SessionId);
        }

        if (invocationLink is null)
        {
            RunWaitState? packedWait = PackedToolApproval.FindActiveWait(
                runWaitStateStore,
                request.Id,
                continuationRunId: null);
            string journalInterruptId = PackedToolApproval.ResolveJournalInterruptId(packedWait, request.Id);
            if (!string.Equals(journalInterruptId, request.Id, StringComparison.Ordinal))
            {
                invocationLink = invocationLinkStore.GetByApprovalRequestId(journalInterruptId);
            }
        }

        string? agentId = invocationLink?.ChildAgentId ?? request.AgentId;
        string? sessionId = ResolveRuntimeSessionId(request, sessionIdOverride, invocationLink);
        string? runId = invocationLink?.ChildRunId ?? request.RunId;
        // Owner of the pending approval — not the resolver (decider) when someone else approves.
        // Used for Narad owner stamps and /api/approvals/changes mine-scope routing.
        string? userId = !string.IsNullOrWhiteSpace(request.UserId)
            ? request.UserId
            : userIdOverride;
        string? rootRunId = invocationLink?.RootRunId ?? runId;
        // Nested child HITL (packed leftovers included) carries OuterSessionId even when
        // the AAAT invocation link is keyed only on the pack-head request id.
        string? parentSessionId = invocationLink?.CallerSessionId ?? request.OuterSessionId;

        if (string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(rootRunId))
        {
            throw new InvalidOperationException($"Approval '{request.Id}' has incomplete canonical runtime identity.");
        }

        return new NaradEventContext(
            agentId,
            sessionId,
            userId,
            runId,
            rootRunId,
            invocationLink?.CallerRunId,
            parentSessionId,
            invocationLink?.CallerToolCallId,
            invocationLink?.CallerContinuationRunId,
            TraceId: null);
    }

    private static string? ResolveRuntimeSessionId(
        GatewayToolApprovalRequest request,
        string? sessionIdOverride,
        AgentToolInvocationLink? invocationLink)
        => invocationLink?.ChildSessionId
            ?? (!string.IsNullOrWhiteSpace(request.OuterSessionId) ? request.SessionId : null)
            ?? (!string.IsNullOrWhiteSpace(sessionIdOverride) ? sessionIdOverride : null)
            ?? request.SessionId;

    private ApprovalResolutionEventWork? TryBuildResolutionEvent(
        GatewayToolApprovalRequest request,
        bool approved,
        string? reason,
        string? decidedByDisplayName,
        string resolutionSource,
        bool stopRun,
        NaradEventContext? context,
        string? stopThreadId)
    {
        if (context is null)
        {
            return null;
        }

        _ = reason;
        RunWaitState? packedWait = runWaitStateStore is null
            ? null
            : PackedToolApproval.FindActiveWait(runWaitStateStore, request.Id, continuationRunId: null);
        IReadOnlyList<string> packedIds = PackedToolApproval.ReadPackedRequestIds(packedWait, request.Id);
        string interruptId = PackedToolApproval.ResolveJournalInterruptId(packedWait, request.Id);
        string waitId = NaradEventBodies.ApprovalWaitId(interruptId);

        return BuildResolutionEvent(
            request,
            PackedToolApproval.ResolvePackedInterruptStorageDecision(
                approvalStore,
                packedIds,
                request.Id,
                approved,
                string.Equals(resolutionSource, "system_timeout", StringComparison.Ordinal),
                stopRun),
            decidedByDisplayName,
            resolutionSource,
            stopRun,
            context,
            stopThreadId,
            interruptId,
            waitId,
            packedIds);
    }

    private ApprovalResolutionEventWork BuildResolutionEvent(
        GatewayToolApprovalRequest request,
        string storageDecision,
        string? decidedByDisplayName,
        string resolutionSource,
        bool stopRun,
        NaradEventContext context,
        string? stopThreadId,
        string interruptId,
        string waitId,
        IReadOnlyList<string> packedRequestIds)
    {
        return new(
            $"approval-resolution:{interruptId}",
            interruptId,
            NaradEventBodies.InterruptResolved(
                interruptId,
                waitId,
                PackedToolApproval.ToProtocolDecision(storageDecision),
                members: PackedToolApproval.ToInterruptResolvedMembers(
                    approvalStore,
                    packedRequestIds,
                    request.Id,
                    storageDecision,
                    overlayRemainingPendingAsStopped: stopRun)),
            context,
            request.ToolCallId,
            request.CheckpointId,
            StopRun: stopRun,
            StopThreadId: stopThreadId,
            StopChannel: string.Equals(resolutionSource, "chat", StringComparison.Ordinal) ? "chat" : "system",
            Actor: decidedByDisplayName,
            PackedRequestIds: packedRequestIds);
    }
}

internal static partial class ApprovalResolutionServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Processing approval resolution request {RequestId}; approved={Approved}, source={Source}, stopRun={StopRun}, session={SessionId}")]
    public static partial void ResolveRequested(ILogger logger, string requestId, bool approved, HitlResolutionSource source, bool stopRun, string? sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Approval resolution request {RequestId} was not found")]
    public static partial void ResolveNotFound(ILogger logger, string requestId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Approval resolution request {RequestId} was already resolved with decision {Decision}")]
    public static partial void ResolveAlreadyResolved(ILogger logger, string requestId, string decision);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Approval resolution store race for request {RequestId}; latestDecision={Decision}")]
    public static partial void ResolveStoreRace(ILogger logger, string requestId, string? decision);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Completed approval resolution request {RequestId}; approved={Approved}, session={SessionId}, target={TargetType}:{TargetId}, user={UserId}")]
    public static partial void ResolveCompleted(ILogger logger, string requestId, bool approved, string? sessionId, string? targetType, string? targetId, string? userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Nested approval {RequestId} has no durable AAAT invocation lineage; falling back to child session {ChildSessionId} (outer={OuterSessionId})")]
    public static partial void MissingInvocationLineage(ILogger logger, string requestId, string outerSessionId, string? childSessionId);
}
