using System.Runtime.CompilerServices;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed record ApprovalContinuationStreamRequest(
    string TargetType,
    string TargetId,
    string SessionId,
    ToolApprovalResponseInfo ApprovalResponse,
    string? UserId);

internal interface IApprovalContinuationOrchestrator
{
    IAsyncEnumerable<NaradPersistedEvent> StreamAsync(ApprovalContinuationStreamRequest request, CancellationToken cancellationToken);
}

internal sealed class ApprovalContinuationOrchestrator(
    IChatGatewayService chatGatewayService,
    IAgentToolInvocationLinkStore invocationLinkStore,
    IAgentToolInvocationStore invocationStore,
    IGatewayApprovalStore approvalStore,
    IRunWaitStateStore runWaitStateStore,
    IGatewaySessionStore sessionStore,
    IRuntimeEventHistoryReader runtimeEventHistoryReader,
    ILogger<ApprovalContinuationOrchestrator> logger) : IApprovalContinuationOrchestrator
{
    // Soft mutex only: exclusive parent re-entry while a stream is live. Journal authority seals
    // without waiting for this window, so reclaim never rewrites a finished parent run.
    private static readonly TimeSpan parentCompletionClaimDuration = TimeSpan.FromSeconds(30);

    public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
        ApprovalContinuationStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AgentToolInvocationLink? linkedInvocation = invocationLinkStore.GetByApprovalRequestId(request.ApprovalResponse.RequestId);
        AgentToolInvocation? durableInvocation = linkedInvocation?.InvocationId is { Length: > 0 } linkedInvocationId
            ? invocationStore.Get(linkedInvocationId)
            : null;
        RunWaitState? approvalWaitState = GetApprovalWaitState(request.ApprovalResponse);
        bool hasDurableChildCompletion = durableInvocation is not null
            && !string.IsNullOrWhiteSpace(durableInvocation.ChildResultJson)
            && !string.IsNullOrWhiteSpace(durableInvocation.ChildOutcome);
        if (approvalWaitState is null && !hasDurableChildCompletion)
        {
            throw new InvalidOperationException($"Approval '{request.ApprovalResponse.RequestId}' has no active wait state.");
        }

        ToolApprovalResponseInfo approvalResponse = approvalWaitState is null
            ? request.ApprovalResponse
            : request.ApprovalResponse with { ContinuationRunId = approvalWaitState.ContinuationRunId };
        if (TryResolveCallerContinuation(approvalResponse.RequestId, out AgentToolInvocationLink? invocationLink, out GatewaySession? callerSession, out RunWaitState? parentWaitState)
            && invocationLink is not null
            && callerSession is not null
            && parentWaitState is not null)
        {
            approvalResponse = approvalResponse with { RootRunId = invocationLink.RootRunId };
            ApprovalContinuationOrchestratorLog.CallerContinuationStarting(
                logger,
                approvalResponse.RequestId,
                invocationLink.ChildSessionId ?? request.SessionId,
                callerSession.Id,
                parentWaitState.ContinuationRunId);

            string invocationId = invocationLink.InvocationId
                ?? throw new InvalidOperationException("AAAT approval link has no stable invocation id.");
            string childResultJson;
            string childOutcome;
            if (hasDurableChildCompletion)
            {
                AgentToolInvocation persisted = durableInvocation
                    ?? throw new InvalidOperationException($"AAAT invocation '{invocationId}' has no durable child completion.");
                childResultJson = persisted.ChildResultJson!;
                childOutcome = persisted.ChildOutcome!;
                if (approvalWaitState is not null)
                {
                    runWaitStateStore.MarkCompleted(approvalWaitState.WaitId);
                }
            }
            else
            {
                AaatChildContinuationResult innerContinuation = await ContinueChildAsync(
                    request,
                    approvalResponse,
                    invocationLink,
                    callerSession,
                    invocationId,
                    cancellationToken).ConfigureAwait(false);

                if (innerContinuation.PendingApprovalEvent is not null)
                {
                    ApprovalContinuationOrchestratorLog.CallerContinuationSkippedNested(
                        logger,
                        approvalResponse.RequestId,
                        request.SessionId);
                    yield return innerContinuation.PendingApprovalEvent;
                    yield break;
                }

                childOutcome = GetNegativeApprovalOutcome(approvalResponse) ?? "completed";
                childResultJson = CreateAgentToolResultPayload(
                    innerContinuation.OutputText,
                    string.Equals(childOutcome, "completed", StringComparison.Ordinal) ? null : childOutcome);

                // ContinueChildAsync records on RunFinished via beforeWaitTerminal so a crash between
                // child terminal and parent re-entry still leaves durable child completion. Prefer
                // that row when present to avoid a second RecordChildCompletion with a mismatched payload.
                AgentToolInvocation? afterChild = invocationStore.Get(invocationId);
                if (afterChild?.ChildResultJson is { Length: > 0 }
                    && afterChild.ChildOutcome is { Length: > 0 }
                    && afterChild.State is AgentToolInvocationStates.ParentCompletionPending
                        or AgentToolInvocationStates.CompletingParent
                        or AgentToolInvocationStates.Completed)
                {
                    childResultJson = afterChild.ChildResultJson;
                    childOutcome = afterChild.ChildOutcome;
                }
                else
                {
                    invocationStore.RecordChildCompletion(invocationId, childResultJson, childOutcome);
                }
            }

            string? childTerminalOutcome = string.Equals(childOutcome, "completed", StringComparison.Ordinal)
                ? null
                : childOutcome;

            if (TrySealParentCompletionFromAuthority(invocationId, invocationLink, childTerminalOutcome))
            {
                yield break;
            }

            string completionLeaseOwner = $"continuation:{Environment.ProcessId}:{Guid.NewGuid():N}";
            if (!invocationStore.TryClaimParentCompletion(invocationId, completionLeaseOwner, parentCompletionClaimDuration))
            {
                if (TrySealParentCompletionFromAuthority(invocationId, invocationLink, childTerminalOutcome))
                {
                    yield break;
                }

                AgentToolInvocation? current = invocationStore.Get(invocationId);
                if (current?.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.Failed)
                {
                    yield break;
                }

                // Another worker is actively finishing the parent. Soft-fail so the job retries
                // quickly instead of hard-failing while a multi-minute lease drains.
                throw new InvalidOperationException(
                    $"AAAT invocation '{invocationId}' parent completion is in progress.");
            }

            // Re-check under the claim: a concurrent worker may have journaled authority before we claimed.
            if (TrySealParentCompletionFromAuthority(invocationId, invocationLink, childTerminalOutcome))
            {
                yield break;
            }

            // Child success or stop/error: always inject the parent tool result and continue the parent LLM.
            // Stop-run ends the *child* run only; the parent receives that outcome as tool output.
            ToolApprovalResponseInfo callerApprovalResponse = approvalResponse with
            {
                ToolCallId = invocationLink.CallerToolCallId!,
                ToolName = invocationLink.CallerToolName!,
                ArgumentsJson = ApprovalContinuationOrchestratorHelpers.TryReadCallerToolArguments(parentWaitState, invocationLink.CallerToolCallId!),
                RunId = invocationLink.CallerRunId,
                RootRunId = invocationLink.RootRunId,
                CheckpointId = null,
                Approved = true,
                Reason = childTerminalOutcome,
                ResultJson = childResultJson,
                StopRun = false,
                ContinuationRunId = parentWaitState.ContinuationRunId,
                TerminalOutcomeScope = null,
            };

            bool invocationStateFinalized = false;
            await foreach (NaradPersistedEvent evt in chatGatewayService.StreamChatWithApprovalAsync(
                callerSession.TargetType,
                callerSession.TargetId,
                callerSession.Id,
                callerApprovalResponse,
                request.UserId,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!invocationStateFinalized && IsRunTerminal(evt.EventType))
                {
                    FinalizeInvocationState(invocationId, completionLeaseOwner, childTerminalOutcome);
                    invocationStateFinalized = true;
                }

                yield return evt;
            }

            if (!invocationStateFinalized)
            {
                FinalizeInvocationState(invocationId, completionLeaseOwner, childTerminalOutcome);
            }

            yield break;
        }

        // Nested AAAT with a link but no durable parent waiting_on_child pause is owned by the
        // in-process agent-tool awaiter. Do not stream the child onto a parent continuation, and do
        // not return an empty "success" stream — that completes the durable job while looking like
        // a dead model turn. Throw a stable miss so the dispatcher completes without retry.
        if (linkedInvocation is not null)
        {
            ApprovalContinuationOrchestratorLog.CallerContinuationNotAvailable(
                logger,
                approvalResponse.RequestId,
                linkedInvocation.CallerSessionId,
                linkedInvocation.CallerContinuationRunId,
                linkedInvocation.CallerToolCallId);
            throw new InvalidOperationException(
                $"AAAT approval '{approvalResponse.RequestId}' resume is owned by the in-process awaiter.");
        }

        await foreach (NaradPersistedEvent evt in chatGatewayService.StreamChatWithApprovalAsync(
            request.TargetType,
            request.TargetId,
            request.SessionId,
            approvalResponse,
            request.UserId,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private RunWaitState? GetApprovalWaitState(ToolApprovalResponseInfo approvalResponse)
        => PackedToolApproval.FindActiveWait(
            runWaitStateStore,
            approvalResponse.RequestId,
            approvalResponse.ContinuationRunId);

    private async Task<AaatChildContinuationResult> ContinueChildAsync(
        ApprovalContinuationStreamRequest request,
        ToolApprovalResponseInfo approvalResponse,
        AgentToolInvocationLink invocationLink,
        GatewaySession callerSession,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var collector = new AaatChildContinuationCollector(
            invocationStore,
            approvalStore,
            GetNegativeApprovalOutcome(approvalResponse) is not null
                ? approvalResponse.ToolCallId
                : null);
        ValueTask RecordBeforeWaitTerminalAsync(NaradPersistedEvent terminalEvent, CancellationToken _)
        {
            if (!string.Equals(terminalEvent.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal))
            {
                return ValueTask.CompletedTask;
            }

            collector.ObserveTerminal(terminalEvent);
            AaatChildContinuationResult completed = collector.ToResult();
            string outcome = GetNegativeApprovalOutcome(approvalResponse) ?? "completed";
            string resultJson = CreateAgentToolResultPayload(
                completed.OutputText,
                string.Equals(outcome, "completed", StringComparison.Ordinal) ? null : outcome);
            invocationStore.RecordChildCompletion(invocationId, resultJson, outcome);
            return ValueTask.CompletedTask;
        }

        using (AgentToolInvocationContext.Push(
            invocationLink.CallerSessionId,
            callerSession.TargetId,
            request.UserId,
            invocationLink.CallerRunId,
            invocationLink.CallerToolCallId,
            invocationLink.RootRunId))
        {
            string childSessionId = invocationLink.ChildSessionId
                ?? throw new InvalidOperationException($"AAAT approval link for '{approvalResponse.RequestId}' is missing child session id.");
            return await collector.CollectAsync(
                chatGatewayService.StreamChatWithApprovalAsync(
                    request.TargetType,
                    request.TargetId,
                    childSessionId,
                    approvalResponse,
                    request.UserId,
                    RecordBeforeWaitTerminalAsync,
                    cancellationToken),
                invocationLink,
                childSessionId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryResolveCallerContinuation(
        string requestId,
        out AgentToolInvocationLink? invocationLink,
        out GatewaySession? callerSession,
        out RunWaitState? parentWaitState)
    {
        invocationLink = invocationLinkStore.GetByApprovalRequestId(requestId);
        callerSession = null;
        parentWaitState = ResolveActiveParentWaitState(invocationLink);

        if (invocationLink is null
            || string.IsNullOrWhiteSpace(invocationLink.CallerToolCallId)
            || string.IsNullOrWhiteSpace(invocationLink.CallerToolName)
            || string.IsNullOrWhiteSpace(invocationLink.CallerRunId)
            || string.IsNullOrWhiteSpace(invocationLink.ChildRunId)
            || string.IsNullOrWhiteSpace(invocationLink.ChildAgentId)
            || string.IsNullOrWhiteSpace(invocationLink.RootRunId)
            || parentWaitState is null
            || !sessionStore.TryGetSession(invocationLink.CallerSessionId, out GatewaySession? resolvedCallerSession)
            || resolvedCallerSession is null)
        {
            ApprovalContinuationOrchestratorLog.CallerContinuationNotAvailable(
                logger,
                requestId,
                invocationLink?.CallerSessionId,
                invocationLink?.CallerContinuationRunId,
                invocationLink?.CallerToolCallId);
            return false;
        }

        callerSession = resolvedCallerSession;
        return true;
    }

    private RunWaitState? ResolveActiveParentWaitState(AgentToolInvocationLink? invocationLink)
    {
        if (invocationLink is null)
        {
            return null;
        }

        // Only a durable waiting_on_child pause can own parent re-entry. CallerContinuationRunId
        // often still points at the outer invoke_agent_* approval wait while AAAT is in-flight;
        // treating that as a parent pause races the live tool invoke and fails the parent run.
        RunWaitState? parentWaitState = null;
        if (invocationLink.CallerContinuationRunId is { Length: > 0 } callerContinuationRunId)
        {
            RunWaitState? byContinuation = runWaitStateStore.GetActiveByContinuationRunId(callerContinuationRunId);
            if (byContinuation is not null
                && string.Equals(byContinuation.WaitReason, RunWaitReasons.Child, StringComparison.Ordinal))
            {
                parentWaitState = byContinuation;
            }
        }

        if (parentWaitState is not null
            || string.IsNullOrWhiteSpace(invocationLink.CallerSessionId)
            || string.IsNullOrWhiteSpace(invocationLink.CallerToolCallId))
        {
            return parentWaitState;
        }

        // Parent AAAT waits are keyed by caller session + invoke_agent_* tool call. Continuation
        // run ids can diverge across replay, but the durable child-pause row remains authoritative.
        return runWaitStateStore.GetActiveChildPauseForCaller(
            invocationLink.CallerSessionId,
            invocationLink.CallerToolCallId);
    }

    private static string CreateAgentToolResultPayload(string outputText, string? outcome = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["result"] = outputText,
        };
        if (outcome is not null)
        {
            payload["outcome"] = outcome;
        }

        return JsonSerializer.Serialize(payload, KautukaJsonContext.Default.DictionaryStringString);
    }

    // Only stop-run is a negative *run* outcome. Deny feeds the current run's tool and continues the LLM.
    private static string? GetNegativeApprovalOutcome(ToolApprovalResponseInfo approvalResponse)
        => approvalResponse.StopRun
            || string.Equals(approvalResponse.Reason, "stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(approvalResponse.Reason, "cancelled", StringComparison.OrdinalIgnoreCase)
                ? "stopped"
                : null;

    private static bool IsRunTerminal(string eventType)
        => string.Equals(eventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
            || string.Equals(eventType, NaradEventTypes.RunError, StringComparison.Ordinal);

    private void FinalizeInvocationState(string invocationId, string completionLeaseOwner, string? childTerminalOutcome)
    {
        if (childTerminalOutcome is null)
        {
            invocationStore.MarkCompleted(invocationId, completionLeaseOwner);
        }
        else
        {
            invocationStore.MarkFailed(invocationId, childTerminalOutcome);
        }
    }

    private bool TrySealParentCompletionFromAuthority(
        string invocationId,
        AgentToolInvocationLink invocationLink,
        string? childTerminalOutcome)
    {
        if (string.IsNullOrWhiteSpace(invocationLink.RootRunId)
            || string.IsNullOrWhiteSpace(invocationLink.CallerToolCallId))
        {
            return false;
        }

        IReadOnlyList<NaradPersistedEvent> history = runtimeEventHistoryReader.ReadRootHistory(invocationLink.RootRunId);
        if (!AaatParentCompletionAuthority.HasAuthoritativeParentCompletion(
                history,
                invocationLink.RootRunId,
                invocationLink.CallerToolCallId!))
        {
            return false;
        }

        if (childTerminalOutcome is null)
        {
            if (!invocationStore.TrySealCompleted(invocationId))
            {
                AgentToolInvocation? current = invocationStore.Get(invocationId);
                return current?.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.Failed;
            }

            return true;
        }

        invocationStore.MarkFailed(invocationId, childTerminalOutcome);
        return true;
    }

}

internal static partial class ApprovalContinuationOrchestratorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Caller continuation is not available for request {RequestId}; callerSessionId={CallerSessionId}, callerContinuationRunId={CallerContinuationRunId}, callerToolCallId={CallerToolCallId}")]
    private static partial void CallerContinuationNotAvailableCore(ILogger logger, string requestId, string? callerSessionId, string? callerContinuationRunId, string? callerToolCallId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting caller continuation for request {RequestId}; childSessionId={ChildSessionId}, callerSessionId={CallerSessionId}, callerContinuationRunId={CallerContinuationRunId}")]
    private static partial void CallerContinuationStartingCore(ILogger logger, string requestId, string childSessionId, string callerSessionId, string callerContinuationRunId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Skipping caller continuation for request {RequestId} because a nested approval was encountered in child session {ChildSessionId}")]
    private static partial void CallerContinuationSkippedNestedCore(ILogger logger, string requestId, string childSessionId);


    public static void CallerContinuationNotAvailable(ILogger logger, string requestId, string? callerSessionId, string? callerContinuationRunId, string? callerToolCallId)
    {
        CallerContinuationNotAvailableCore(logger, requestId, callerSessionId, callerContinuationRunId, callerToolCallId);
    }

    public static void CallerContinuationStarting(ILogger logger, string requestId, string childSessionId, string callerSessionId, string callerContinuationRunId)
    {
        CallerContinuationStartingCore(logger, requestId, childSessionId, callerSessionId, callerContinuationRunId);
    }

    public static void CallerContinuationSkippedNested(ILogger logger, string requestId, string childSessionId)
    {
        CallerContinuationSkippedNestedCore(logger, requestId, childSessionId);
    }

}
