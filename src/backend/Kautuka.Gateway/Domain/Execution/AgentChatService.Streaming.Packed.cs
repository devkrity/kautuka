using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Observability;

using Microsoft.Extensions.AI;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{
    private sealed class PackedStreamIo
    {
        public required AgentExecutionEventWriter EventWriter { get; init; }
        public required ChannelWriter<NaradPersistedEvent> Writer { get; init; }
        public required NaradEventContext RuntimeEventContext { get; init; }
        public required AssistantMessageLifecycle MessageLifecycle { get; init; }
        public required List<GatewayToolCall> CompletedToolCalls { get; init; }
        public required AgentRuntime Runtime { get; init; }
        public required string AgentId { get; init; }
        public required string SessionId { get; init; }
        public required string? UserId { get; init; }
        public required string RunId { get; init; }
        public required Func<string, string, string, CancellationToken, ValueTask> EmitToolCallStart { get; init; }
        public required Func<string, string, CancellationToken, ValueTask> PushAgentToolCallerScope { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public bool SuppressToolFailureAssistantOutput { get; set; }
    }

    private readonly record struct PackedApprovalFinalizeOutcome(
        AgentRunExecutionSummary? AutoContinuation,
        int HitlRequestCount,
        bool PauseForHitl);

    private async Task<ToolApprovalResponseInfo> JournalPackedToolLifecycleAsync(
        PackedStreamIo io,
        ToolApprovalResponseInfo head)
    {
        List<ToolApprovalResponseInfo> pack = [.. head.EnumeratePack()];
        IRunWaitStateStore waitStore = GetRunWaitStateStore();
        RunWaitState? wait = PackedToolApproval.FindActiveWait(
            waitStore,
            head.RequestId,
            head.ContinuationRunId);
        if (wait is not null)
        {
            wait = waitStore.GetByWaitId(wait.WaitId) ?? wait;
        }

        for (int index = 0; index < pack.Count; index++)
        {
            pack[index] = PackedToolApproval.ApplyStoredResult(wait, pack[index]);
        }

        // Packed starts first (approved and denied), then finishes. Deny is a terminal
        // outcome — never emit tool.call.finished without a matching started for that call id.
        for (int index = 0; index < pack.Count; index++)
        {
            ToolApprovalResponseInfo current = pack[index];
            if (string.IsNullOrWhiteSpace(current.ToolCallId))
            {
                continue;
            }

            bool alreadyStarted = current.ToolLifecycleAlreadyJournaled
                || await HasExistingToolCallInSessionHistoryAsync(
                    io.Runtime,
                    current.ToolCallId,
                    io.CancellationToken).ConfigureAwait(false);
            if (alreadyStarted)
            {
                continue;
            }

            // Spawn AAAT only for approved invokes; denied agent tools still get start→finish.
            if (current.Approved && ActionIdentity.IsAgentToolInvoke(current.ToolName))
            {
                await io.PushAgentToolCallerScope(current.ToolCallId, current.ToolName, io.CancellationToken)
                    .ConfigureAwait(false);
            }

            await io.EmitToolCallStart(
                current.ToolCallId,
                current.ToolName,
                current.ArgumentsJson ?? "{}",
                io.CancellationToken).ConfigureAwait(false);
        }

        var leftoverInvokes = new List<(int Index, Func<CancellationToken, Task<string>> Invoke)>();
        Func<CancellationToken, Task<string>> CreateIndependentInvoke(ToolApprovalResponseInfo item)
            => ct => packedApprovedToolInvoker!.InvokeAsync(
                io.AgentId,
                io.SessionId,
                io.UserId,
                item.ToolName,
                item.ArgumentsJson,
                ct).AsTask();
        for (int index = 0; index < pack.Count; index++)
        {
            ToolApprovalResponseInfo current = pack[index];
            if (!current.Approved)
            {
                if (current.ToolLifecycleAlreadyJournaled)
                {
                    if (!string.IsNullOrWhiteSpace(current.ResultJson))
                    {
                        ChatExecutionHelpers.RecordToolCallResult(
                            io.CompletedToolCalls,
                            current.ToolCallId,
                            current.ResultJson);
                        io.SuppressToolFailureAssistantOutput =
                            !ChatExecutionHelpers.CanEmitAssistantOutput(io.CompletedToolCalls);
                    }

                    continue;
                }

                ToolApprovalResponseInfo denied = await JournalDeniedPackedToolAsync(io, current)
                    .ConfigureAwait(false);
                wait ??= PackedToolApproval.FindActiveWait(
                    waitStore,
                    denied.RequestId,
                    denied.ContinuationRunId);
                if (wait is not null && !string.IsNullOrWhiteSpace(denied.ResultJson))
                {
                    waitStore.PutPackedToolResult(
                        wait.WaitId,
                        denied.RequestId,
                        denied.ResultJson,
                        journaledStart: true);
                    wait = waitStore.GetByWaitId(wait.WaitId) ?? wait;
                }

                pack[index] = denied;
                continue;
            }

            if (current.ToolLifecycleAlreadyJournaled)
            {
                if (!string.IsNullOrWhiteSpace(current.ResultJson))
                {
                    ChatExecutionHelpers.RecordToolCallResult(
                        io.CompletedToolCalls,
                        current.ToolCallId,
                        current.ResultJson);
                    io.SuppressToolFailureAssistantOutput =
                        !ChatExecutionHelpers.CanEmitAssistantOutput(io.CompletedToolCalls);
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(current.ResultJson)
                || packedApprovedToolInvoker is null
                || string.IsNullOrWhiteSpace(current.ToolCallId)
                || !PackedToolApproval.CanExecuteIndependently(current.ToolName))
            {
                continue;
            }

            leftoverInvokes.Add((index, CreateIndependentInvoke(current)));
        }

        if (leftoverInvokes.Count > 0)
        {
            int maxParallel = toolInvocationSettingsResolver?.ResolveForAgent(io.AgentId).MaxParallelTools ?? 0;
            string[] executed = await PackedToolApproval.InvokeThrottledAsync(
                leftoverInvokes.Select(static item => item.Invoke).ToList(),
                maxParallel,
                io.CancellationToken).ConfigureAwait(false);
            for (int leftoverIndex = 0; leftoverIndex < leftoverInvokes.Count; leftoverIndex++)
            {
                int packIndex = leftoverInvokes[leftoverIndex].Index;
                string resultJson = executed[leftoverIndex];
                ToolApprovalResponseInfo current = pack[packIndex];
                wait ??= PackedToolApproval.FindActiveWait(
                    waitStore,
                    current.RequestId,
                    current.ContinuationRunId);
                if (wait is not null)
                {
                    waitStore.PutPackedToolResult(wait.WaitId, current.RequestId, resultJson, journaledStart: true);
                    wait = waitStore.GetByWaitId(wait.WaitId) ?? wait;
                }

                pack[packIndex] = current with { ResultJson = resultJson };
            }
        }

        for (int index = 0; index < pack.Count; index++)
        {
            ToolApprovalResponseInfo current = pack[index];
            if (!current.Approved || current.ToolLifecycleAlreadyJournaled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(current.ResultJson))
            {
                continue;
            }

            ChatExecutionHelpers.RecordToolCallResult(io.CompletedToolCalls, current.ToolCallId, current.ResultJson);
            io.SuppressToolFailureAssistantOutput = !ChatExecutionHelpers.CanEmitAssistantOutput(io.CompletedToolCalls);
            await io.EventWriter.WriteAsync(
                io.Writer,
                new GatewayChatToolResult(current.ToolCallId, current.ResultJson),
                io.RuntimeEventContext,
                io.MessageLifecycle,
                io.CancellationToken).ConfigureAwait(false);
            pack[index] = current with { ToolLifecycleAlreadyJournaled = true };
        }

        ToolApprovalResponseInfo nextHead = pack[0];
        return pack.Count == 1
            ? nextHead
            : nextHead with { PackedSiblingResponses = pack.Skip(1).ToList() };
    }

    private static async Task<ToolApprovalResponseInfo> JournalDeniedPackedToolAsync(
        PackedStreamIo io,
        ToolApprovalResponseInfo current)
    {
        if (string.IsNullOrWhiteSpace(current.ResultJson))
        {
            current = current with
            {
                ResultJson = PackedToolApproval.CreateOutcomeResultJson(
                    current.ToolName,
                    current.Expired ? "expired" : "denied",
                    current.Reason),
            };
        }

        ChatExecutionHelpers.RecordToolCallResult(io.CompletedToolCalls, current.ToolCallId, current.ResultJson);
        io.SuppressToolFailureAssistantOutput = !ChatExecutionHelpers.CanEmitAssistantOutput(io.CompletedToolCalls);
        // Start was emitted in the packed start pass (or already present for non-defer tools).
        // Always close the open call — deny/expired is finished(denied), never a lone finish.
        string denyMessage = TryReadDeniedToolMessage(current.ResultJson)
            ?? $"Tool '{current.ToolName}' was denied by the user.";
        await io.EventWriter.WriteAsync(
            io.Writer,
            new GatewayChatToolFailure(current.ToolCallId, current.ToolName, "denied", denyMessage),
            io.RuntimeEventContext,
            io.MessageLifecycle,
            io.CancellationToken).ConfigureAwait(false);

        return current with { ToolLifecycleAlreadyJournaled = true };
    }

    private async Task<PackedApprovalFinalizeOutcome> FinalizePackedApprovalRequestsAsync(
        PackedStreamIo io,
        List<ToolApprovalRequestContent> packedRequests,
        string? continuationRunId,
        AgentContinuationRequest? continuationRequest,
        ResolvedRunLimits limits,
        ActiveRunBudget runBudget,
        Activity? runActivity,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        List<GatewayConversationAsset> checkpointGeneratedAssets,
        long runStartTicks,
        DateTimeOffset runStartedAt)
    {
        string? approvalUserId = io.UserId;
        if (string.IsNullOrWhiteSpace(approvalUserId)
            && GetSessionStore().TryGetSession(io.SessionId, out GatewaySession? approvalSession)
            && approvalSession is not null)
        {
            approvalUserId = approvalSession.OwnerUserId;
        }

        PlatformConfiguration platformConfiguration = platformConfigurationProvider.GetCurrent();
        AgentToolInvocationContextState? agentToolInvocationContext = AgentToolInvocationContext.Current;
        var autoResponses = new List<ToolApprovalResponseInfo>();
        var packedRequestIds = new List<string>(packedRequests.Count);
        ToolApprovalRequestContent? firstHitl = null;
        string? firstHitlCallId = null;
        string? firstHitlToolName = null;
        var hitlMembers = new List<PackedApprovalInterruptMember>();
        NaradPersistedEvent? firstHitlEvent = null;
        int hitlRequestCount = 0;

        foreach (ToolApprovalRequestContent tarc in packedRequests)
        {
            string callId = tarc.ToolCall is FunctionCallContent fcc
                ? (fcc.CallId ?? tarc.RequestId)
                : tarc.RequestId;
            string toolName = tarc.ToolCall is FunctionCallContent fcc2 ? fcc2.Name : "unknown";
            string? argsJson = tarc.ToolCall is FunctionCallContent fcc3 && fcc3.Arguments is not null
                ? JsonSerializer.Serialize(fcc3.Arguments, KautukaJsonContext.Default.Object)
                : null;
            packedRequestIds.Add(tarc.RequestId);

            string? mcpServerId = HitlApprovalIdentity.ResolveMcpServerId(platformConfiguration, io.AgentId, toolName);
            long? agentDefinitionRevision = TryResolveAgentDefinitionRevision(io.AgentId);
            if (!io.Runtime.RequireApprovalForAllTools && !ToolApprovalPolicyEvaluator.RequiresHumanApproval(
                toolExecutionPolicyService,
                platformConfiguration,
                io.AgentId,
                toolName,
                argsJson))
            {
                AgentChatServiceLog.AutoApprovingToolByPolicy(logger, tarc.RequestId, toolName, io.AgentId, io.RunId);
                approvalStore.CreateRequest(new GatewayToolApprovalRequest(
                    Id: tarc.RequestId,
                    SessionId: io.SessionId,
                    RunId: io.RunId,
                    CheckpointId: null,
                    AgentId: io.AgentId,
                    UserId: approvalUserId,
                    ToolCallId: callId,
                    ToolName: toolName,
                    ToolType: ActionIdentity.ResolveKind(toolName),
                    McpServerId: mcpServerId,
                    ArgumentsJson: argsJson,
                    RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
                    Decision: "auto_approved",
                    DecidedBy: "policy:auto",
                    DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
                    Reason: "Approved automatically because tool is not configured for human approval",
                    ExpiresAt: null,
                    AgentDefinitionRevision: agentDefinitionRevision));
                autoResponses.Add(new ToolApprovalResponseInfo(
                    tarc.RequestId,
                    callId,
                    toolName,
                    argsJson,
                    true,
                    "policy:auto",
                    io.RunId));
                continue;
            }

            hitlRequestCount++;
            string toolType = ActionIdentity.ResolveKind(toolName);
            hitlMembers.Add(new PackedApprovalInterruptMember(
                tarc.RequestId,
                callId,
                toolName,
                argsJson,
                toolType));
            approvalStore.CreateRequest(new GatewayToolApprovalRequest(
                Id: tarc.RequestId,
                SessionId: io.SessionId,
                RunId: io.RunId,
                CheckpointId: null,
                AgentId: io.AgentId,
                UserId: approvalUserId,
                ToolCallId: callId,
                ToolName: toolName,
                ToolType: toolType,
                McpServerId: mcpServerId,
                ArgumentsJson: argsJson,
                RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
                Decision: null,
                DecidedBy: null,
                DecidedAt: null,
                Reason: null,
                ExpiresAt: FormatHitlExpiresAt(limits.HitlExpiry),
                OuterSessionId: agentToolInvocationContext?.CallerSessionId,
                OuterAgentId: agentToolInvocationContext?.CallerAgentId,
                AgentDefinitionRevision: agentDefinitionRevision));

            AgentChatServiceLog.PublishingHitlApprovalRequested(logger, tarc.RequestId, toolName, io.RunId, io.UserId);
            try
            {
                await mediator.Publish(new HitlApprovalRequested(
                    tarc.RequestId,
                    callId,
                    toolName,
                    argsJson,
                    io.SessionId,
                    "agent",
                    io.AgentId,
                    approvalUserId,
                    RunId: io.RunId,
                    ToolType: toolType,
                    OuterSessionId: agentToolInvocationContext?.CallerSessionId,
                    OuterAgentId: agentToolInvocationContext?.CallerAgentId), CancellationToken.None).ConfigureAwait(false);
                AgentChatServiceLog.PublishedHitlApprovalRequested(logger, tarc.RequestId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AgentChatServiceLog.HitlChannelNotifyFailed(logger, ex, tarc.RequestId);
            }

            if (firstHitl is null)
            {
                firstHitl = tarc;
                firstHitlCallId = callId;
                firstHitlToolName = toolName;
            }
        }

        if (firstHitl is not null)
        {
            Dictionary<string, PackedToolResult> packedResults = new(StringComparer.Ordinal);
            PackedApprovalInterruptMember hitlPackHead = hitlMembers[0];
            GatewayChatApprovalRequest packJournal = new(
                hitlPackHead.RequestId,
                hitlPackHead.ToolCallId,
                hitlPackHead.ToolName,
                hitlPackHead.ArgumentsJson,
                io.RunId,
                CheckpointId: null,
                ToolType: hitlPackHead.ToolType,
                ContinuationRunId: continuationRunId,
                OuterSessionId: agentToolInvocationContext?.CallerSessionId,
                OuterAgentId: agentToolInvocationContext?.CallerAgentId,
                TraceId: runActivity?.TraceId.ToString(),
                AgentId: io.AgentId,
                SessionId: io.SessionId,
                PauseRun: true,
                Members: hitlMembers.Count > 1 ? hitlMembers : null);
            // Journal interrupt.requested before independent auto-invokes so a slow
            // connector fetch cannot hide the HITL pack on the live journal/SSE.
            firstHitlEvent = (await io.EventWriter.WriteAsync(
                io.Writer,
                packJournal,
                io.RuntimeEventContext,
                io.MessageLifecycle,
                io.CancellationToken).ConfigureAwait(false))!;

            if (packedApprovedToolInvoker is not null)
            {
                foreach (ToolApprovalResponseInfo autoResponse in autoResponses)
                {
                    if (!PackedToolApproval.CanExecuteIndependently(autoResponse.ToolName))
                    {
                        continue;
                    }

                    await io.EmitToolCallStart(
                        autoResponse.ToolCallId,
                        autoResponse.ToolName,
                        autoResponse.ArgumentsJson ?? "{}",
                        io.CancellationToken).ConfigureAwait(false);
                    string executed = await packedApprovedToolInvoker.InvokeAsync(
                        io.AgentId,
                        io.SessionId,
                        io.UserId,
                        autoResponse.ToolName,
                        autoResponse.ArgumentsJson,
                        io.CancellationToken).ConfigureAwait(false);
                    ChatExecutionHelpers.RecordToolCallResult(
                        io.CompletedToolCalls,
                        autoResponse.ToolCallId,
                        executed);
                    await io.EventWriter.WriteAsync(
                        io.Writer,
                        new GatewayChatToolResult(autoResponse.ToolCallId, executed),
                        io.RuntimeEventContext,
                        io.MessageLifecycle,
                        io.CancellationToken).ConfigureAwait(false);
                    packedResults[autoResponse.RequestId] = new PackedToolResult(executed, JournaledStart: true);
                }
            }

            if (!string.IsNullOrWhiteSpace(continuationRunId))
            {
                TimeSpan activeElapsed = runBudget.CaptureElapsed(continuationRequest?.ActiveElapsed ?? TimeSpan.Zero);
                runBudget.Pause();
                string sessionStateJson = await SerializeSessionStateJsonAsync(io.Runtime, io.CancellationToken)
                    .ConfigureAwait(false);
                string behaviorJson = RunClockEnvelope.Wrap(
                    JsonSerializer.Serialize(io.Runtime.EffectiveBehavior, KautukaJsonContext.Default.EffectiveRunBehavior),
                    activeElapsed,
                    packedRequestIds,
                    packedResults.Count > 0 ? packedResults : null);
                GetRunWaitStateStore().PauseRunForApproval(new RunWaitStateCreateRequest(
                    continuationRunId,
                    io.RunId,
                    "agent",
                    io.AgentId,
                    io.SessionId,
                    io.AgentId,
                    approvalUserId,
                    firstHitl.RequestId,
                    sessionStateJson,
                    behaviorJson,
                    BuildArtifactManifestJson(checkpointGeneratedAssets),
                    ToolCallId: firstHitlCallId,
                    ToolName: firstHitlToolName,
                    ApprovalRequestId: firstHitl.RequestId,
                    RootRunId: io.RuntimeEventContext.RootRunId,
                    CanonicalEventId: firstHitlEvent!.EventId,
                    TraceContext: AgentRunHostCallTracer.CaptureRunTraceContext(runActivity, io.RunId)));
            }

            return new PackedApprovalFinalizeOutcome(null, hitlRequestCount, PauseForHitl: true);
        }

        if (autoResponses.Count == 0)
        {
            return new PackedApprovalFinalizeOutcome(null, hitlRequestCount, PauseForHitl: false);
        }

        foreach (ToolApprovalResponseInfo autoResponse in autoResponses)
        {
            await io.EmitToolCallStart(
                autoResponse.ToolCallId,
                autoResponse.ToolName,
                autoResponse.ArgumentsJson ?? "{}",
                io.CancellationToken).ConfigureAwait(false);
        }

        ToolApprovalResponseInfo packHead = autoResponses[0];
        if (autoResponses.Count > 1)
        {
            packHead = packHead with { PackedSiblingResponses = autoResponses.Skip(1).ToList() };
        }

        AgentRunExecutionSummary continuation = await ProduceAgentStreamAsync(
            io.Writer,
            io.Runtime,
            io.AgentId,
            io.SessionId,
            string.Empty,
            inputAssets,
            workspaceFiles,
            packHead,
            continuationRequest,
            io.RunId,
            runStartTicks,
            runStartedAt,
            io.CancellationToken,
            io.UserId,
            io.RuntimeEventContext,
            io.MessageLifecycle,
            runActivity).ConfigureAwait(false);
        return new PackedApprovalFinalizeOutcome(continuation, hitlRequestCount, PauseForHitl: false);
    }
}
