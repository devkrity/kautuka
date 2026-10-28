using System.Diagnostics;
using System.Text;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Observability;

using Mediator;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed class ApprovalContinuationDispatcherService(
    IApprovalContinuationQueue queue,
    IGatewayApprovalStore approvalStore,
    IApprovalContinuationOrchestrator approvalContinuationOrchestrator,
    IRunWaitStateStore runWaitStateStore,
    IAgentToolInvocationLinkStore invocationLinkStore,
    IMediator mediator,
    ILogger<ApprovalContinuationDispatcherService> logger,
    IPackedApprovedToolInvoker? packedApprovedToolInvoker = null,
    IToolInvocationSettingsResolver? toolInvocationSettingsResolver = null,
    IRuntimeEventHistoryReader? runtimeEventHistoryReader = null) : BackgroundService
{
    private readonly string leaseOwner = $"gateway-{Environment.ProcessId}-{Guid.NewGuid():N}";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each per-lease heartbeat CancellationTokenSource is disposed by the nested finally block that owns its heartbeat task.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "The per-lease finally awaits the heartbeat before either cancellation source is disposed.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ApprovalContinuationLease lease in queue.LeaseAllAsync(leaseOwner, stoppingToken).ConfigureAwait(false))
            {
                ApprovalContinuationWork work = lease.Work;
                Task? heartbeatTask = null;

                ApprovalContinuationDispatcherServiceLog.DispatchDequeued(
                    logger,
                    work.RequestId,
                    work.SessionId,
                    work.TargetType,
                    work.TargetId,
                    work.Approved,
                    work.StopRun);

                using IDisposable scope = logger.BeginKautukaCorrelationScope(
                    ("RequestId", work.RequestId),
                    ("TargetType", work.TargetType),
                    ("TargetId", work.TargetId),
                    ("SessionId", work.SessionId),
                    ("UserId", work.UserId));

                var heartbeatCancellation = new CancellationTokenSource();
                using var continuationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                RunWaitState? waitState = null;
                string? rootRunId = null;
                try
                {
                    GatewayToolApprovalRequest? approval = approvalStore.GetById(work.RequestId);
                    if (approval is null)
                    {
                        ApprovalContinuationDispatcherServiceLog.SkippingMissingApprovalRequest(logger, work.RequestId);
                        RunWaitState? orphanedWait = PackedToolApproval.FindActiveWait(
                            runWaitStateStore,
                            work.RequestId,
                            continuationRunId: null);
                        if (orphanedWait is not null)
                        {
                            runWaitStateStore.MarkFailed(orphanedWait.WaitId, "approval_record_missing");
                        }
                        await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }

                    waitState = PackedToolApproval.FindActiveWait(
                        runWaitStateStore,
                        work.RequestId,
                        continuationRunId: null);
                    rootRunId = waitState?.RootRunId ?? approval.RunId;
                    if (FindRootTerminal(rootRunId) is { } existingTerminal)
                    {
                        CloseWaitAfterTerminal(waitState, existingTerminal);
                        await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }
                    heartbeatTask = MaintainLeaseAsync(lease, waitState?.WaitId, rootRunId,
                        continuationCancellation, heartbeatCancellation.Token);
                    await MaterializeDecidedPackedGapsAsync(
                        waitState,
                        work,
                        continuationCancellation.Token).ConfigureAwait(false);
                    waitState = PackedToolApproval.FindActiveWait(
                        runWaitStateStore,
                        work.RequestId,
                        continuationRunId: null);
                    IReadOnlyList<string> packedRequestIds = PackedToolApproval.ReadPackedRequestIds(
                        waitState,
                        work.RequestId);
                    if (waitState is not null
                        && PackedToolApproval.HasUndecidedHumanSiblings(
                            approvalStore,
                            packedRequestIds,
                            work.RequestId))
                    {
                        ApprovalContinuationDispatcherServiceLog.HoldingPackedApprovalUntilSiblingsDecide(
                            logger,
                            work.RequestId,
                            waitState.WaitId,
                            packedRequestIds.Count);
                        await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }

                    if (waitState is not null
                        && !runWaitStateStore.TryClaimExecution(waitState.WaitId, leaseOwner, TimeSpan.FromMinutes(2)))
                    {
                        throw new InvalidOperationException("The approval wait could not be claimed for execution.");
                    }
                    using Activity? dispatchScope = waitState is null
                        ? null
                        : AgentRunHostCallTracer.StartBackgroundDispatchScope(
                            "approval continuation dispatch",
                            waitState.RunId,
                            waitState.TraceParent,
                            waitState.TraceState);

                    var approvalResponse = PackedToolApproval.AttachPackResponses(
                        approvalStore,
                        waitState,
                        new ToolApprovalResponseInfo(
                            approval.Id,
                            approval.ToolCallId,
                            approval.ToolName,
                            approval.ArgumentsJson,
                            work.Approved,
                            work.Reason,
                            RunId: approval.RunId,
                            CheckpointId: null,
                            StopRun: work.StopRun,
                            ContinuationRunId: waitState?.ContinuationRunId,
                            Expired: work.Source == HitlResolutionSource.SystemTimeout));

                    ApprovalContinuationDispatcherServiceLog.DispatchUsingDirectContinuation(logger, work.RequestId, work.SessionId, work.TargetType, work.TargetId);
                    (string outputText, bool hasNestedApprovalRequest, string sessionId, string targetType, string targetId) continuation = await CollectOutputAsync(
                        new ApprovalContinuationStreamRequest(
                            work.TargetType,
                            work.TargetId,
                            work.SessionId,
                            approvalResponse,
                            work.UserId),
                        continuationCancellation.Token).ConfigureAwait(false);

                    if (waitState is not null && continuation.hasNestedApprovalRequest)
                    {
                        // AAAT nested boundaries pause only the child run on the wire. The parent
                        // invoke_agent_* tool stays open, but the claimed outer approval wait must
                        // leave Executing so later inner approvals can resume caller lineage.
                        runWaitStateStore.ReleaseExecutingWait(waitState.WaitId);
                    }

                    await mediator.Publish(new HitlContinuationCompleted(
                        work.RequestId,
                        continuation.sessionId,
                        continuation.targetType,
                        continuation.targetId,
                        work.UserId,
                        work.Approved,
                        work.Source,
                        continuation.hasNestedApprovalRequest,
                        continuation.outputText),
                        CancellationToken.None).ConfigureAwait(false);

                    await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                    await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);

                    ApprovalContinuationDispatcherServiceLog.DispatchCompleted(
                        logger,
                        work.RequestId,
                        continuation.sessionId,
                        continuation.hasNestedApprovalRequest,
                        continuation.outputText.Length);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested
                    && continuationCancellation.IsCancellationRequested
                    && FindRootTerminal(rootRunId) is not null)
                {
                    // Authority can terminate a run outside this stream (stop/recovery). Its
                    // continuation must release the dispatcher, never retry or emit a second final.
                    await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                    CloseWaitAfterTerminal(waitState, FindRootTerminal(rootRunId)!);
                    await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TerminalContinuationException ex)
                {
                    ApprovalContinuationDispatcherServiceLog.ApprovalContinuationDispatchFailed(logger, ex, work.RequestId);
                    await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                    await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    await mediator.Publish(new HitlContinuationFailed(
                        work.RequestId,
                        work.SessionId,
                        work.TargetType,
                        work.TargetId,
                        work.UserId,
                        work.Approved,
                        ex.PublicReason),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    && IsNonRetryableContinuationMiss(ex))
                {
                    // Stale durable jobs (wait already cleared) or AAAT resumes owned by the live
                    // in-process awaiter must not FailAsync-retry into a storm.
                    ApprovalContinuationDispatcherServiceLog.ApprovalContinuationDispatchFailed(logger, ex, work.RequestId);
                    await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                    await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Provider/HttpClient timeouts are TaskCanceledException (OCE). Retry the job
                    // instead of escaping ExecuteAsync and stopping the Gateway host (StopHost).
                    ApprovalContinuationDispatcherServiceLog.ApprovalContinuationDispatchFailed(logger, ex, work.RequestId);
                    try
                    {
                        await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                        ApprovalContinuationFailureDisposition disposition = await queue
                            .FailAsync(lease, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None)
                            .ConfigureAwait(false);
                        RunWaitState? failedWait = waitState
                            ?? PackedToolApproval.FindActiveWait(
                                runWaitStateStore,
                                work.RequestId,
                                continuationRunId: null);
                        if (failedWait is not null)
                        {
                            if (disposition is ApprovalContinuationFailureDisposition.DeadLettered)
                            {
                                runWaitStateStore.MarkFailed(failedWait.WaitId, "continuation_retries_exhausted");
                            }
                            else
                            {
                                runWaitStateStore.ReleaseForRetry(failedWait.WaitId, leaseOwner);
                            }
                        }
                        if (disposition is ApprovalContinuationFailureDisposition.DeadLettered)
                        {
                            await mediator.Publish(new HitlContinuationFailed(
                                work.RequestId,
                                work.SessionId,
                                work.TargetType,
                                work.TargetId,
                                work.UserId,
                                work.Approved,
                                "Continuation failed after exhausting durable retries."),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                    {
                        ApprovalContinuationDispatcherServiceLog.ApprovalContinuationLeaseUpdateFailed(
                            logger,
                            persistenceException,
                            work.RequestId);
                    }
                }
                finally
                {
                    try
                    {
                        await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        heartbeatCancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
    }

    private static bool IsNonRetryableContinuationMiss(Exception exception)
    {
        string message = exception.Message;
        return message.Contains("has no active wait state", StringComparison.Ordinal)
            || message.Contains("owned by the in-process awaiter", StringComparison.Ordinal);
    }

    private async Task MaintainLeaseAsync(
        ApprovalContinuationLease lease,
        string? waitId,
        string? rootRunId,
        CancellationTokenSource continuationCancellation,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (FindRootTerminal(rootRunId) is not null)
                {
                    await continuationCancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
                if (++ticks < 30) continue;
                ticks = 0;
                await queue.RenewAsync(lease, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(waitId))
                {
                    continue;
                }

                RunWaitState? waitState = runWaitStateStore.GetByWaitId(waitId);
                if (waitState is { Status: RunWaitStatuses.Executing })
                {
                    runWaitStateStore.RenewExecutionLease(waitState.WaitId, lease.LeaseOwner, TimeSpan.FromMinutes(2));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed monitor must stop this continuation. Its per-work failure handler
            // owns durable retry; rethrowing here would also fault every cleanup await.
            ApprovalContinuationDispatcherServiceLog.ApprovalContinuationLeaseUpdateFailed(logger, ex, lease.Work.RequestId);
            await continuationCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private NaradPersistedEvent? FindRootTerminal(string? rootRunId)
        => string.IsNullOrWhiteSpace(rootRunId) ? null
            : runtimeEventHistoryReader?.ReadRootHistory(rootRunId).LastOrDefault(item =>
                string.Equals(item.RunId, rootRunId, StringComparison.Ordinal)
                && NaradEventTypes.IsRootRunTerminalEventType(item.EventType));

    private void CloseWaitAfterTerminal(RunWaitState? wait, NaradPersistedEvent terminal)
    {
        if (wait is null) return;
        if (terminal.EventType == NaradEventTypes.RunFinished) runWaitStateStore.MarkCompleted(wait.WaitId);
        else runWaitStateStore.MarkFailed(wait.WaitId, "continuation_run_terminal");
    }

    private static async Task StopHeartbeatAsync(
        CancellationTokenSource cancellation,
        Task? heartbeatTask)
    {
        if (heartbeatTask is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await heartbeatTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<(string OutputText, bool HasNestedApprovalRequest, string SessionId, string TargetType, string TargetId)> CollectOutputAsync(
        ApprovalContinuationStreamRequest request,
        CancellationToken cancellationToken)
    {
        StringBuilder output = new();
        bool hasNestedApprovalRequest = false;
        GatewayChatResult? finalResult = null;
        string? lastToolResult = null;
        string? terminalEventType = null;

        ApprovalContinuationDispatcherServiceLog.InnerContinuationCollecting(
            logger,
            request.SessionId,
            request.TargetType,
            request.TargetId,
            request.ApprovalResponse.RequestId,
            request.ApprovalResponse.ToolCallId,
            request.ApprovalResponse.CheckpointId,
            request.ApprovalResponse.ResultJson);

        await foreach (NaradPersistedEvent evt in approvalContinuationOrchestrator.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(evt.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
            {
                GatewayChatApprovalRequest approvalRequest = NaradInterruptObservation.ResolveChatApprovalRequest(
                    evt,
                    approvalStore,
                    fallbackAgentId: request.TargetId,
                    fallbackSessionId: request.SessionId);
                hasNestedApprovalRequest = true;
                ApprovalContinuationDispatcherServiceLog.InnerContinuationNestedApproval(
                    logger,
                    request.SessionId,
                    request.TargetType,
                    request.TargetId,
                    approvalRequest.RequestId,
                    approvalRequest.ToolCallId,
                    approvalRequest.ToolName);
                continue;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.MessageTextDelta, StringComparison.Ordinal))
            {
                GatewayChatTokenDelta delta = NaradStreamObservation.ReadBodyPayload(evt, KautukaJsonContext.Default.GatewayChatTokenDelta);
                ApprovalContinuationDispatcherServiceLog.InnerContinuationToken(
                    logger,
                    request.SessionId,
                    request.TargetType,
                    request.TargetId,
                    delta.Delta);
                output.Append(delta.Delta);
                continue;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal)
                && string.Equals(NaradStreamObservation.ReadBodyString(evt, "outcome"), "success", StringComparison.Ordinal))
            {
                GatewayChatToolResult toolResult = NaradStreamObservation.ReadBodyPayload(evt, KautukaJsonContext.Default.GatewayChatToolResult);
                string? resultJson = toolResult.ResultJson;
                if (string.IsNullOrWhiteSpace(resultJson))
                {
                    continue;
                }
                ApprovalContinuationDispatcherServiceLog.InnerContinuationToolResult(
                    logger,
                    request.SessionId,
                    request.TargetType,
                    request.TargetId,
                    resultJson);
                lastToolResult = resultJson;
                continue;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
            {
                terminalEventType = evt.EventType;
                string terminalTargetId = NaradStreamObservation.ReadString(evt, "agentId") ?? request.TargetId;
                GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(
                    evt,
                    request.TargetType,
                    terminalTargetId,
                    evt.SessionId);
                finalResult = done.Result;
                ApprovalContinuationDispatcherServiceLog.InnerContinuationStreamDone(
                    logger,
                    done.Result.SessionId,
                    done.Result.TargetType,
                    done.Result.TargetId,
                    done.Result.Response.Length,
                    done.Result.ToolCalls?.Count ?? 0,
                    hasNestedApprovalRequest);
            }
        }

        if (string.Equals(terminalEventType, NaradEventTypes.RunError, StringComparison.Ordinal))
        {
            throw new TerminalContinuationException(
                "continuation_run_failed",
                "The continuation run failed.");
        }

        if (string.Equals(terminalEventType, NaradEventTypes.RunPaused, StringComparison.Ordinal)
            && !hasNestedApprovalRequest)
        {
            hasNestedApprovalRequest = HasDurableNestedApprovalBoundary(request);
            if (!hasNestedApprovalRequest)
            {
                throw new TerminalContinuationException(
                    "pause_boundary_missing",
                    "The continuation paused without a recoverable approval boundary.");
            }
        }

        if (!hasNestedApprovalRequest)
        {
            // Child-session HITL does not emit parent run.paused. Recover nested AAAT boundaries
            // from durable child pause / approval lineage instead.
            hasNestedApprovalRequest = HasDurableNestedApprovalBoundary(request);
        }

        string outputText = !string.IsNullOrWhiteSpace(finalResult?.Response)
            ? finalResult.Response
            : !string.IsNullOrWhiteSpace(output.ToString())
                ? output.ToString()
                : finalResult?.ToolCalls?
                    .LastOrDefault(static call => !string.IsNullOrWhiteSpace(call.ResultJson))?
                    .ResultJson
                    ?? lastToolResult
                    ?? string.Empty;

        ApprovalContinuationDispatcherServiceLog.InnerContinuationCollected(
            logger,
            hasNestedApprovalRequest ? request.SessionId : finalResult?.SessionId ?? request.SessionId,
            hasNestedApprovalRequest ? request.TargetType : finalResult?.TargetType ?? request.TargetType,
            hasNestedApprovalRequest ? request.TargetId : finalResult?.TargetId ?? request.TargetId,
            output.Length,
            finalResult?.Response.Length ?? 0,
            outputText.Length,
            hasNestedApprovalRequest);

        // Nested approval pauses are child-session terminals. The completion event still reports
        // the outer continuation work identity that the dispatcher was asked to finish.
        return (
            outputText,
            hasNestedApprovalRequest,
            hasNestedApprovalRequest ? request.SessionId : finalResult?.SessionId ?? request.SessionId,
            hasNestedApprovalRequest ? request.TargetType : finalResult?.TargetType ?? request.TargetType,
            hasNestedApprovalRequest ? request.TargetId : finalResult?.TargetId ?? request.TargetId);
    }

    private bool HasDurableNestedApprovalBoundary(ApprovalContinuationStreamRequest request)
    {
        AgentToolInvocationLink? link = invocationLinkStore.GetByApprovalRequestId(request.ApprovalResponse.RequestId);
        if (link is null
            || string.IsNullOrWhiteSpace(link.CallerSessionId)
            || string.IsNullOrWhiteSpace(link.CallerToolCallId))
        {
            return false;
        }

        RunWaitState? childPause = runWaitStateStore.GetActiveChildPauseForCaller(
            link.CallerSessionId,
            link.CallerToolCallId);
        if (childPause is not
            {
                ApprovalRequestId.Length: > 0,
                ChildSessionId.Length: > 0,
                CanonicalEventId.Length: > 0,
            }
            || string.Equals(
                childPause.ApprovalRequestId,
                request.ApprovalResponse.RequestId,
                StringComparison.Ordinal))
        {
            return false;
        }

        GatewayToolApprovalRequest? nestedApproval = approvalStore.GetById(childPause.ApprovalRequestId);
        return nestedApproval is { Decision: null }
            && string.Equals(nestedApproval.SessionId, childPause.ChildSessionId, StringComparison.Ordinal);
    }

    private async Task MaterializeDecidedPackedGapsAsync(
        RunWaitState? waitState,
        ApprovalContinuationWork triggering,
        CancellationToken cancellationToken)
    {
        if (waitState is null)
        {
            return;
        }

        IReadOnlyList<string> packedIds = PackedToolApproval.ReadPackedRequestIds(waitState, triggering.RequestId);
        RunWaitState? wait = runWaitStateStore.GetByWaitId(waitState.WaitId) ?? waitState;
        IPackedApprovedToolInvoker? invoker = packedApprovedToolInvoker;
        bool packComplete = !PackedToolApproval.HasUndecidedHumanSiblings(
            approvalStore,
            packedIds,
            triggering.RequestId);
        var independentInvokes = new List<(GatewayToolApprovalRequest Row, ApprovalContinuationWork Work)>();
        foreach (string requestId in packedIds)
        {
            GatewayToolApprovalRequest? row = approvalStore.GetById(requestId);
            if (row is null || string.IsNullOrWhiteSpace(row.Decision))
            {
                continue;
            }

            ApprovalContinuationWork memberWork = ContinuationWorkForStoredDecision(triggering, row);
            if (RunClockEnvelope.TryReadPackedToolResults(wait.BehaviorJson, out IReadOnlyDictionary<string, PackedToolResult> existing)
                && existing.ContainsKey(memberWork.RequestId))
            {
                continue;
            }

            if (!memberWork.Approved
                || !PackedToolApproval.CanExecuteIndependently(row.ToolName)
                || invoker is null
                || string.IsNullOrWhiteSpace(row.AgentId)
                || string.IsNullOrWhiteSpace(row.SessionId))
            {
                await MaterializePackedDecisionAsync(wait, row, memberWork, cancellationToken).ConfigureAwait(false);
                wait = runWaitStateStore.GetByWaitId(waitState.WaitId) ?? wait;
                continue;
            }

            if (!packComplete)
            {
                continue;
            }

            independentInvokes.Add((row, memberWork));
        }

        if (independentInvokes.Count == 0 || invoker is null)
        {
            return;
        }

        ApprovalContinuationDispatcherServiceLog.InvokingPackedIndependentToolsInParallel(
            logger,
            waitState.WaitId,
            independentInvokes.Count);
        int maxParallel = toolInvocationSettingsResolver?
            .ResolveForAgent(independentInvokes[0].Row.AgentId)
            .MaxParallelTools ?? 0;
        string[] executed = await PackedToolApproval.InvokeThrottledAsync(
            independentInvokes.Select(item => (Func<CancellationToken, Task<string>>)(ct =>
                invoker.InvokeAsync(
                    item.Row.AgentId!,
                    item.Row.SessionId!,
                    item.Work.UserId ?? item.Row.UserId,
                    item.Row.ToolName,
                    item.Row.ArgumentsJson,
                    ct).AsTask())).ToList(),
            maxParallel,
            cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < independentInvokes.Count; index++)
        {
            (GatewayToolApprovalRequest row, ApprovalContinuationWork work) = independentInvokes[index];
            wait = runWaitStateStore.GetByWaitId(waitState.WaitId) ?? wait;
            if (wait is null)
            {
                break;
            }

            if (RunClockEnvelope.TryReadPackedToolResults(wait.BehaviorJson, out IReadOnlyDictionary<string, PackedToolResult> existing)
                && existing.ContainsKey(work.RequestId))
            {
                continue;
            }

            runWaitStateStore.PutPackedToolResult(wait.WaitId, work.RequestId, executed[index], journaledStart: false);
            ApprovalContinuationDispatcherServiceLog.ExecutedPackedToolBeforeModel(
                logger,
                work.RequestId,
                wait.WaitId,
                row.ToolName);
        }
    }

    private static ApprovalContinuationWork ContinuationWorkForStoredDecision(
        ApprovalContinuationWork triggering,
        GatewayToolApprovalRequest row)
    {
        string decision = row.Decision ?? string.Empty;
        bool approved = string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "auto_approved", StringComparison.OrdinalIgnoreCase);
        bool expired = string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase);
        bool stopRun = string.Equals(decision, "stopped", StringComparison.OrdinalIgnoreCase);
        return triggering with
        {
            RequestId = row.Id,
            Approved = approved && !stopRun,
            Reason = row.Reason,
            Source = expired ? HitlResolutionSource.SystemTimeout : triggering.Source,
            StopRun = stopRun,
        };
    }

    private async Task MaterializePackedDecisionAsync(
        RunWaitState? waitState,
        GatewayToolApprovalRequest approval,
        ApprovalContinuationWork work,
        CancellationToken cancellationToken)
    {
        if (waitState is null)
        {
            return;
        }

        if (RunClockEnvelope.TryReadPackedToolResults(waitState.BehaviorJson, out IReadOnlyDictionary<string, PackedToolResult> existing)
            && existing.ContainsKey(work.RequestId))
        {
            return;
        }

        if (!work.Approved)
        {
            string outcome = work.Source == HitlResolutionSource.SystemTimeout ? "expired" : "denied";
            string resultJson = PackedToolApproval.CreateOutcomeResultJson(approval.ToolName, outcome, work.Reason);
            runWaitStateStore.PutPackedToolResult(waitState.WaitId, work.RequestId, resultJson, journaledStart: false);
            return;
        }

        if (!PackedToolApproval.CanExecuteIndependently(approval.ToolName)
            || packedApprovedToolInvoker is null
            || string.IsNullOrWhiteSpace(approval.AgentId)
            || string.IsNullOrWhiteSpace(approval.SessionId))
        {
            return;
        }

        string executedJson = await packedApprovedToolInvoker.InvokeAsync(
            approval.AgentId,
            approval.SessionId,
            work.UserId ?? approval.UserId,
            approval.ToolName,
            approval.ArgumentsJson,
            cancellationToken).ConfigureAwait(false);
        runWaitStateStore.PutPackedToolResult(waitState.WaitId, work.RequestId, executedJson, journaledStart: false);
        ApprovalContinuationDispatcherServiceLog.ExecutedPackedToolBeforeModel(
            logger,
            work.RequestId,
            waitState.WaitId,
            approval.ToolName);
    }

    private sealed class TerminalContinuationException : InvalidOperationException
    {
        public TerminalContinuationException()
            : this("continuation_failed", "The continuation failed.")
        {
        }

        public TerminalContinuationException(string message)
            : this(message, "The continuation failed.")
        {
        }

        public TerminalContinuationException(string message, Exception innerException)
            : base(message, innerException)
        {
            PublicReason = "The continuation failed.";
        }

        public TerminalContinuationException(string failureCode, string publicReason)
            : base(failureCode)
        {
            PublicReason = publicReason;
        }

        public string PublicReason { get; }
    }
}

internal static partial class ApprovalContinuationDispatcherServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Skipping continuation for missing approval request {RequestId}")]
    public static partial void SkippingMissingApprovalRequest(ILogger logger, string requestId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Approval continuation dispatch failed for request {RequestId}")]
    public static partial void ApprovalContinuationDispatchFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Critical, Message = "Approval continuation lease state could not be updated for request {RequestId}")]
    public static partial void ApprovalContinuationLeaseUpdateFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Dequeued approval continuation request {RequestId} for session {SessionId}, target {TargetType}:{TargetId}; approved={Approved}, stopRun={StopRun}")]
    public static partial void DispatchDequeued(ILogger logger, string requestId, string sessionId, string targetType, string targetId, bool approved, bool stopRun);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Using stop-run short circuit for approval continuation request {RequestId} in session {SessionId}")]
    public static partial void DispatchStopRun(ILogger logger, string requestId, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Using direct continuation for approval request {RequestId} in session {SessionId}, target {TargetType}:{TargetId}")]
    public static partial void DispatchUsingDirectContinuation(ILogger logger, string requestId, string sessionId, string targetType, string targetId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Holding model resume for packed approval {RequestId} on wait {WaitId} until {PackedCount} packed HITL rows are decided")]
    public static partial void HoldingPackedApprovalUntilSiblingsDecide(ILogger logger, string requestId, string waitId, int packedCount);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Executed packed tool {ToolName} for approval {RequestId} on wait {WaitId} before the model turn")]
    public static partial void ExecutedPackedToolBeforeModel(ILogger logger, string requestId, string waitId, string toolName);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Invoking {ToolCount} packed independent tools in parallel on wait {WaitId}")]
    public static partial void InvokingPackedIndependentToolsInParallel(ILogger logger, string waitId, int toolCount);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Completed approval continuation request {RequestId} for session {SessionId}; nestedApproval={HasNestedApproval}, outputLength={OutputLength}")]
    public static partial void DispatchCompleted(ILogger logger, string requestId, string sessionId, bool hasNestedApproval, int outputLength);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Collecting continuation output for session {SessionId}, target {TargetType}:{TargetId}, request {RequestId}, tool call {ToolCallId}; checkpointId={CheckpointId}, hasResultJson={HasResultJson}")]
    private static partial void InnerContinuationCollectingCore(ILogger logger, string sessionId, string targetType, string targetId, string requestId, string toolCallId, string? checkpointId, bool hasResultJson);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Observed nested approval while collecting continuation for session {SessionId}, target {TargetType}:{TargetId}, request {RequestId}, tool call {ToolCallId}, tool {ToolName}")]
    private static partial void InnerContinuationNestedApprovalCore(ILogger logger, string sessionId, string targetType, string targetId, string requestId, string toolCallId, string toolName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Observed continuation token for session {SessionId}, target {TargetType}:{TargetId}; deltaLength={DeltaLength}")]
    private static partial void InnerContinuationTokenCore(ILogger logger, string sessionId, string targetType, string targetId, int deltaLength);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Observed continuation tool result for session {SessionId}, target {TargetType}:{TargetId}; resultLength={ResultLength}")]
    private static partial void InnerContinuationToolResultCore(ILogger logger, string sessionId, string targetType, string targetId, int resultLength);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Observed continuation stream completion for session {SessionId}, target {TargetType}:{TargetId}; responseLength={ResponseLength}, toolCallCount={ToolCallCount}, hasNestedApprovalRequest={HasNestedApprovalRequest}")]
    private static partial void InnerContinuationStreamDoneCore(ILogger logger, string sessionId, string targetType, string targetId, int responseLength, int toolCallCount, bool hasNestedApprovalRequest);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Collected continuation output for session {SessionId}, target {TargetType}:{TargetId}; tokenOutputLength={TokenOutputLength}, finalResponseLength={FinalResponseLength}, selectedOutputLength={SelectedOutputLength}, hasNestedApprovalRequest={HasNestedApprovalRequest}")]
    private static partial void InnerContinuationCollectedCore(ILogger logger, string sessionId, string targetType, string targetId, int tokenOutputLength, int finalResponseLength, int selectedOutputLength, bool hasNestedApprovalRequest);

    public static void InnerContinuationCollecting(ILogger logger, string sessionId, string targetType, string targetId, string requestId, string toolCallId, string? checkpointId, string? resultJson)
    {
        bool hasResultJson = !string.IsNullOrWhiteSpace(resultJson);
        InnerContinuationCollectingCore(logger, sessionId, targetType, targetId, requestId, toolCallId, checkpointId, hasResultJson);
    }

    public static void InnerContinuationNestedApproval(ILogger logger, string sessionId, string targetType, string targetId, string requestId, string toolCallId, string toolName)
    {
        InnerContinuationNestedApprovalCore(logger, sessionId, targetType, targetId, requestId, toolCallId, toolName);
    }

    public static void InnerContinuationToken(ILogger logger, string sessionId, string targetType, string targetId, string delta)
    {
        InnerContinuationTokenCore(logger, sessionId, targetType, targetId, delta.Length);
    }

    public static void InnerContinuationToolResult(ILogger logger, string sessionId, string targetType, string targetId, string resultJson)
    {
        InnerContinuationToolResultCore(logger, sessionId, targetType, targetId, resultJson.Length);
    }

    public static void InnerContinuationStreamDone(ILogger logger, string sessionId, string targetType, string targetId, int responseLength, int toolCallCount, bool hasNestedApprovalRequest)
    {
        InnerContinuationStreamDoneCore(logger, sessionId, targetType, targetId, responseLength, toolCallCount, hasNestedApprovalRequest);
    }

    public static void InnerContinuationCollected(ILogger logger, string sessionId, string targetType, string targetId, int tokenOutputLength, int finalResponseLength, int selectedOutputLength, bool hasNestedApprovalRequest)
    {
        InnerContinuationCollectedCore(logger, sessionId, targetType, targetId, tokenOutputLength, finalResponseLength, selectedOutputLength, hasNestedApprovalRequest);
    }

}
