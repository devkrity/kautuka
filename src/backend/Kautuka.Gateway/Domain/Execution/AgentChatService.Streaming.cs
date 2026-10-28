using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Infrastructure.Observability;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{

    private static readonly ActivitySource agentRunActivitySource = new(KautukaActivitySources.GatewayAgentRunSource);
    private static readonly HashSet<string> exportedParentSources = new(StringComparer.Ordinal)
    {
        KautukaActivitySources.GatewayAgentRunSource,
        "Experimental.Microsoft.Agents.AI",
        "Experimental.Microsoft.Extensions.AI",
    };

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "ProduceAgentStreamAsync is awaited before the run Activity using-scope ends.")]
    public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
        string agentId,
        string sessionId,
        string? userId,
        string runTrigger,
        string message,
        string? stageTransitionId,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        IReadOnlyList<InjectedMemoryFact> memoryFacts,
        ToolApprovalResponseInfo? approvalResponse,
        AgentContinuationRequest? continuationRequest,
        AgentRequestContext? requestContext = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using IDisposable usageScope = GetUsageCaptureAccessor().BeginScope();
        string runId = approvalResponse?.RunId
            ?? requestContext?.RunId
            ?? Guid.NewGuid().ToString("N");
        AgentToolInvocationContextState? parentInvocation = AgentToolInvocationContext.Current;
        string rootRunId = approvalResponse?.RootRunId
            ?? parentInvocation?.RootRunId
            ?? parentInvocation?.CallerRunId
            ?? runId;
        bool isResumeRequest = approvalResponse is not null || continuationRequest?.Enabled == true;
        string deliveryAttemptId = ResolveDeliveryAttemptId(approvalResponse, continuationRequest, runId);
        NaradEventContext runtimeEventContext = new(
            agentId,
            sessionId,
            userId ?? string.Empty,
            runId,
            rootRunId,
            parentInvocation?.CallerRunId,
            parentInvocation?.CallerSessionId,
            parentInvocation?.CallerToolCallId,
            approvalResponse?.ContinuationRunId ?? continuationRequest?.ContinuationRunId,
            runTrigger,
            TraceId: null);
        Activity? previousActivity = Activity.Current;
        bool hasExplicitParentContext = AgentRunHostCallTracer.TryParseRequestTraceContext(requestContext?.Trace, out ActivityContext explicitParentContext);
        // HTTP/SSE is a short inbound span and must not parent the run. HITL resume and nested
        // AAAT join the Gateway run via wait-state / requestContext. Ambient Gateway or MAF
        // sources stay attached so child runs remain on the same trace.
        bool detachFromAmbientParent = !hasExplicitParentContext
            && previousActivity is not null
            && !exportedParentSources.Contains(previousActivity.Source.Name);
        if (detachFromAmbientParent)
        {
            Activity.Current = null;
        }

        using Activity? runActivity = hasExplicitParentContext
            ? agentRunActivitySource.StartActivity(BuildAgentRunSpanName(agentId, runTrigger), ActivityKind.Internal, explicitParentContext)
            : agentRunActivitySource.StartActivity(BuildAgentRunSpanName(agentId, runTrigger), ActivityKind.Internal);
        runActivity?.SetTag("kautuka.agent.id", agentId);
        runActivity?.SetTag("kautuka.agent.name", agentId);
        runActivity?.SetTag("kautuka.run.id", runId);
        runActivity?.SetTag("kautuka.session.id", sessionId);
        runActivity?.SetTag("kautuka.run.trigger", runTrigger);
        runActivity?.SetTag("kautuka.stage_transition_id", stageTransitionId ?? string.Empty);
        if (requestContext?.Trace is AgentRequestTraceContext traceContext)
        {
            runActivity?.SetTag("kautuka.parent.trace_id", traceContext.TraceId);
            runActivity?.SetTag("kautuka.parent.span_id", traceContext.ParentSpanId);
        }
        AgentRunHostCallTracer.MarkAgentRun(runActivity, runId, GetTelemetryTenantId(), userId);
        if (runActivity?.TraceId is ActivityTraceId runTraceId)
        {
            runtimeEventContext = runtimeEventContext with { TraceId = runTraceId.ToString() };
        }

        void RestoreRunActivity() => AgentRunHostCallTracer.RestoreCurrent(runActivity);

        var assistantMessageLifecycle = new AssistantMessageLifecycle(
            BuildAssistantMessageId(rootRunId, runId, deliveryAttemptId));
        AgentExecutionEventWriter earlyEventWriter = CreateExecutionEventWriter();

        try
        {
            if (!isResumeRequest)
            {
                bool skipRunStarted = RunLifecycleEventAlreadyPublished(rootRunId, runId, NaradEventTypes.RunStarted);
                if (!skipRunStarted)
                {
                    await foreach (NaradPersistedEvent started in EmitDomainAsync(
                                       earlyEventWriter,
                                       new GatewayChatRunStarted(
                                           runTrigger,
                                           stageTransitionId,
                                           parentInvocation?.CallerRunId,
                                           parentInvocation?.CallerToolCallId),
                                       runtimeEventContext,
                                       assistantMessageLifecycle,
                                       ct).ConfigureAwait(false))
                    {
                        yield return started;
                        RestoreRunActivity();
                    }
                }

                bool skipUserMessage = RunLifecycleEventAlreadyPublished(rootRunId, runId, NaradEventTypes.MessageUserRecorded);
                if (!skipUserMessage)
                {
                    await foreach (NaradPersistedEvent userEvt in EmitDomainAsync(
                                       earlyEventWriter,
                                       new GatewayChatUserMessageRecorded(
                                           requestContext?.UserMessageId ?? NaradEventBodies.BuildUserMessageId(rootRunId, runId),
                                           message,
                                           inputAssets.Select(static asset => asset.Id).ToArray()),
                                       runtimeEventContext,
                                       assistantMessageLifecycle,
                                       ct).ConfigureAwait(false))
                    {
                        yield return userEvt;
                        RestoreRunActivity();
                    }
                }
            }

            NaradPersistedEvent[]? readinessFailureEvents = await CreateReadinessFailureEventsAsync(
                approvalResponse,
                agentId,
                sessionId,
                runtimeEventContext,
                assistantMessageLifecycle,
                earlyEventWriter,
                ct).ConfigureAwait(false);
            RestoreRunActivity();
            if (readinessFailureEvents is not null)
            {
                foreach (NaradPersistedEvent failureEvent in readinessFailureEvents)
                {
                    yield return failureEvent;
                    RestoreRunActivity();
                }
                yield break;
            }

            // Single run.resumed site (domain map) — open journal pause only.
            if (TryResolveNaradWaitId(approvalResponse, continuationRequest?.ContinuationRunId) is string resumeWaitId
                && HasOpenRunPauseForWait(runtimeEventContext.RootRunId, runtimeEventContext.RunId, resumeWaitId))
            {
                await foreach (NaradPersistedEvent resumed in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatContinuationResumed(
                                       CheckpointId: continuationRequest?.CheckpointId ?? string.Empty,
                                       ContinuationRunId: continuationRequest?.ContinuationRunId
                                           ?? approvalResponse?.ContinuationRunId
                                           ?? runId,
                                       CheckpointIndex: continuationRequest?.CheckpointIndex ?? 0,
                                       BoundaryType: continuationRequest?.BoundaryType ?? RunWaitReasons.Approval,
                                       WaitId: resumeWaitId),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return resumed;
                    RestoreRunActivity();
                }
            }

            // Do not open an assistant text lifecycle on pure HITL/AAAT resume — gate-before-start
            // must surface tool.call.started first, not an empty message.text.started.
            if (!isResumeRequest)
            {
                await foreach (NaradPersistedEvent textStarted in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatMessageTextStarted(),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return textStarted;
                    RestoreRunActivity();
                }
            }

            long runStartTicks = Stopwatch.GetTimestamp();
            DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(continuationRequest?.ContinuationRunId))
            {
            }
            if (continuationRequest?.CheckpointIndex is int resumedCheckpointIndex)
            {
            }
            if (!string.IsNullOrWhiteSpace(continuationRequest?.ResumeToken))
            {
            }

            // Stop-run: terminate THIS run. Deny: feed a tool outcome and continue the LLM below.
            if (approvalResponse is { StopRun: true })
            {
                string stoppedMsg = string.Equals(approvalResponse.TerminalOutcomeScope, "child_agent", StringComparison.Ordinal)
                    ? "The child agent run was stopped."
                    : approvalResponse.Expired
                        ? $"Tool '{approvalResponse.ToolName}' expired without a decision. The run was aborted."
                        : $"Tool '{approvalResponse.ToolName}' was stopped. The operation has been stopped.";
                string failureCode = approvalResponse.Expired ? "expired" : "stopped";
                if (!string.IsNullOrWhiteSpace(approvalResponse.ToolCallId)
                    && !string.Equals(approvalResponse.TerminalOutcomeScope, "child_agent", StringComparison.Ordinal))
                {
                    await foreach (NaradPersistedEvent toolStart in EmitDomainAsync(
                                       earlyEventWriter,
                                       new GatewayChatToolCall(
                                           approvalResponse.ToolCallId,
                                           approvalResponse.ToolName,
                                           approvalResponse.ArgumentsJson ?? "{}",
                                           ActionIdentity.ResolveKind(approvalResponse.ToolName),
                                           ParentToolCallId: parentInvocation?.CallerToolCallId,
                                           ParentMessageId: assistantMessageLifecycle.MessageId),
                                       runtimeEventContext,
                                       assistantMessageLifecycle,
                                       ct).ConfigureAwait(false))
                    {
                        yield return toolStart;
                        RestoreRunActivity();
                    }

                    await foreach (NaradPersistedEvent toolFinish in EmitDomainAsync(
                                       earlyEventWriter,
                                       new GatewayChatToolFailure(
                                           approvalResponse.ToolCallId,
                                           approvalResponse.ToolName,
                                           failureCode,
                                           stoppedMsg),
                                       runtimeEventContext,
                                       assistantMessageLifecycle,
                                       ct).ConfigureAwait(false))
                    {
                        yield return toolFinish;
                        RestoreRunActivity();
                    }
                }

                double stoppedDurationMs = Stopwatch.GetElapsedTime(runStartTicks).TotalMilliseconds;
                GetAgentRunMetrics().RecordRunCompleted(
                    new AgentRunMetricContext(agentId, runTrigger, null, null),
                    new AgentRunMetricObservation("stopped", (long)stoppedDurationMs, null, 0, 0, null, 0, 0));
                await foreach (NaradPersistedEvent delta in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatTokenDelta(stoppedMsg, false),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return delta;
                    RestoreRunActivity();
                }

                await foreach (NaradPersistedEvent done in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatStreamDone(new GatewayChatResult(
                                       "agent",
                                       agentId,
                                       sessionId,
                                       stoppedMsg,
                                       false,
                                       new GatewayMetrics(0, 0, 0),
                                       null,
                                       null,
                                       null,
                                       null,
                                       GetUsageCaptureAccessor().Snapshot(),
                                       runActivity?.TraceId.ToString(),
                                       new GatewayRunState(GatewayRunPhases.Stopped))),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return done;
                    RestoreRunActivity();
                }

                yield break;
            }

            // Expiry without abort: feed an expired tool outcome so produce resumes the model.
            if (approvalResponse is { Approved: false, Expired: true, ToolCallId.Length: > 0 }
                && string.IsNullOrWhiteSpace(approvalResponse.ResultJson))
            {
                string expiredMsg = $"Tool '{approvalResponse.ToolName}' expired without a decision.";
                approvalResponse = approvalResponse with
                {
                    ResultJson = JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["result"] = expiredMsg,
                            ["outcome"] = "expired",
                        },
                        KautukaJsonContext.Default.DictionaryStringString),
                    Reason = approvalResponse.Reason ?? "expired",
                };
            }

            // Deny without a prebuilt ResultJson: inject a durable denied tool outcome so produce resumes the model.
            if (approvalResponse is { Approved: false, Expired: false, ToolCallId.Length: > 0 }
                && string.IsNullOrWhiteSpace(approvalResponse.ResultJson))
            {
                string deniedMsg = $"Tool '{approvalResponse.ToolName}' was denied by the user.";
                approvalResponse = approvalResponse with
                {
                    ResultJson = JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["result"] = deniedMsg,
                            ["outcome"] = "denied",
                        },
                        KautukaJsonContext.Default.DictionaryStringString),
                    Reason = approvalResponse.Reason ?? "denied",
                };
            }

            bool isContinuationResume = approvalResponse is not null
                || continuationRequest?.Enabled == true;
            AgentRuntimeResult runtimeResult = await runtimeFactory.BuildAsync(agentId, sessionId, userId, stageTransitionId, memoryFacts, isContinuationResume, string.Equals(runTrigger, "workflow_test", StringComparison.Ordinal), continuationRequest?.SessionStateJson, continuationRequest?.EffectiveBehaviorJson, ct).ConfigureAwait(false);
            RestoreRunActivity();
            if (!runtimeResult.IsSuccess)
            {
                double runtimeInitFailedDurationMs = Stopwatch.GetElapsedTime(runStartTicks).TotalMilliseconds;
                GetAgentRunMetrics().RecordRunCompleted(
                    new AgentRunMetricContext(agentId, runTrigger, null, null),
                    new AgentRunMetricObservation("runtime_init_failed", (long)runtimeInitFailedDurationMs, null, 0, 0, null, 0, 0));
                await foreach (NaradPersistedEvent delta in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatTokenDelta($"[{runtimeResult.ErrorMessage}]", false),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return delta;
                    RestoreRunActivity();
                }

                await foreach (NaradPersistedEvent failed in EmitDomainAsync(
                                   earlyEventWriter,
                                   new GatewayChatStreamDone(Error("agent", agentId, sessionId, runtimeResult.ErrorMessage)),
                                   runtimeEventContext,
                                   assistantMessageLifecycle,
                                   ct).ConfigureAwait(false))
                {
                    yield return failed;
                    RestoreRunActivity();
                }

                yield break;
            }

            AgentRuntime runtime = runtimeResult.Runtime;
            runActivity?.SetTag("kautuka.agent.name", runtime.AgentName);
            if (runActivity is not null)
            {
                runActivity.DisplayName = BuildAgentRunSpanName(runtime.AgentName, runTrigger);
            }
            GetAgentRunMetrics().RecordRunStarted(new AgentRunMetricContext(
                agentId,
                runTrigger,
                runtime.EffectiveBehavior.ProviderId,
                runtime.EffectiveBehavior.ModelId));

            var channel = Channel.CreateUnbounded<NaradPersistedEvent>(new UnboundedChannelOptions { SingleWriter = false });
            string continuationRunId = continuationRequest?.ContinuationRunId ?? Guid.NewGuid().ToString("N");
            continuationRequest = continuationRequest is null
                ? new AgentContinuationRequest(
                    Enabled: true,
                    ResumeToken: null,
                    ContinuationRunId: continuationRunId)
                : continuationRequest with { ContinuationRunId = continuationRunId };

            Task<AgentRunExecutionSummary> producer = ProduceAgentStreamAsync(
                channel.Writer,
                runtime,
                agentId,
                sessionId,
                message,
                inputAssets,
                workspaceFiles,
                approvalResponse,
                continuationRequest,
                runId,
                runStartTicks,
                runStartedAt,
                ct,
                userId,
                runtimeEventContext,
                assistantMessageLifecycle,
                runActivity);

            RestoreRunActivity();
            await foreach (NaradPersistedEvent evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
                RestoreRunActivity();
            }

            RestoreRunActivity();

            AgentRunExecutionSummary summary = await producer.ConfigureAwait(false);

            GetAgentRunMetrics().RecordRunCompleted(
                new AgentRunMetricContext(
                    agentId,
                    runTrigger,
                    runtime.EffectiveBehavior.ProviderId,
                    runtime.EffectiveBehavior.ModelId),
                new AgentRunMetricObservation(
                    summary.Result,
                    summary.DurationMs,
                    summary.FirstModelOutputLatencyMs,
                    summary.TokenIn,
                    summary.TokenOut,
                    summary.Usage?.ProviderRequestCount,
                    summary.ToolCallCount,
                    summary.ApprovalRequestCount));
        }
        finally
        {
            if (detachFromAmbientParent)
            {
                Activity.Current = previousActivity;
            }
        }
    }

    private static string BuildAgentRunSpanName(string agentNameOrId, string runTrigger)
    {
        string normalizedAgent = string.IsNullOrWhiteSpace(agentNameOrId)
            ? "unknown"
            : agentNameOrId.Trim();
        string normalizedTrigger = string.IsNullOrWhiteSpace(runTrigger)
            ? "unknown"
            : runTrigger.Trim();

        return $"agent ({normalizedAgent}) run [{normalizedTrigger}]";
    }

    private static string BuildAssistantMessageId(string rootRunId, string runId, string deliveryAttemptId)
        => NaradEventBodies.BuildAssistantMessageId(rootRunId, runId, deliveryAttemptId);

    internal static string ResolveDeliveryAttemptId(
        ToolApprovalResponseInfo? approvalResponse,
        AgentContinuationRequest? continuationRequest,
        string runId)
        => continuationRequest?.ContinuationRunId
            ?? approvalResponse?.ContinuationRunId
            ?? approvalResponse?.RequestId
            ?? runId;

    private string? TryResolveNaradWaitId(
        ToolApprovalResponseInfo? approvalResponse,
        string? continuationRunId)
    {
        if (approvalResponse is not null)
        {
            RunWaitState? packedWait = PackedToolApproval.FindActiveWait(
                GetRunWaitStateStore(),
                approvalResponse.RequestId,
                approvalResponse.ContinuationRunId ?? continuationRunId);
            if (packedWait is not null)
            {
                return PackedToolApproval.ResolveJournalWaitId(packedWait, approvalResponse.RequestId);
            }

            if (!string.IsNullOrWhiteSpace(approvalResponse.RequestId))
            {
                return NaradEventBodies.ApprovalWaitId(approvalResponse.RequestId);
            }
        }

        if (string.IsNullOrWhiteSpace(continuationRunId))
        {
            return null;
        }

        RunWaitState? waitState = GetRunWaitStateStore().GetActiveByContinuationRunId(continuationRunId);
        return !string.IsNullOrWhiteSpace(waitState?.ApprovalRequestId)
            ? NaradEventBodies.ApprovalWaitId(waitState.ApprovalRequestId)
            : null;
    }

    /// <summary>
    /// True when journal authority for <paramref name="runId"/> still has an unresumed
    /// <c>run.paused</c> for <paramref name="waitId"/>. Drives resume emission; do not key
    /// off approval request presence or wait-row BoundaryType alone.
    /// </summary>
    private bool HasOpenRunPauseForWait(string rootRunId, string runId, string waitId)
    {
        if (string.IsNullOrWhiteSpace(rootRunId)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(waitId))
        {
            return false;
        }

        bool open = false;
        foreach (NaradPersistedEvent item in GetRuntimeEventHistoryReader().ReadRootHistory(rootRunId))
        {
            if (!string.Equals(item.RunId, runId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(item.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
            {
                string? pausedWaitId = NaradPersistedEventReader.String(
                    NaradPersistedEventReader.ParseBody(item),
                    "waitId");
                if (string.Equals(pausedWaitId, waitId, StringComparison.Ordinal))
                {
                    open = true;
                }
            }
            else if (string.Equals(item.EventType, NaradEventTypes.RunResumed, StringComparison.Ordinal))
            {
                string? resumedWaitId = NaradPersistedEventReader.String(
                    NaradPersistedEventReader.ParseBody(item),
                    "waitId");
                if (string.Equals(resumedWaitId, waitId, StringComparison.Ordinal))
                {
                    open = false;
                }
            }
            else if (string.Equals(item.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                || string.Equals(item.EventType, NaradEventTypes.RunError, StringComparison.Ordinal)
                || string.Equals(item.EventType, NaradEventTypes.RunCancelled, StringComparison.Ordinal))
            {
                open = false;
            }
        }

        return open;
    }

    private bool RunLifecycleEventAlreadyPublished(string rootRunId, string runId, string eventType)
    {
        if (string.IsNullOrWhiteSpace(rootRunId) || string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        return GetRuntimeEventHistoryReader().ReadRootHistory(rootRunId)
            .Any(item => string.Equals(item.RunId, runId, StringComparison.Ordinal)
                && string.Equals(item.EventType, eventType, StringComparison.Ordinal));
    }

    private static JsonElement CreateTokenPayload(GatewayChatTokenDelta delta, string messageId)
        => JsonSerializer.SerializeToElement(new JsonObject
        {
            ["delta"] = delta.Delta,
            ["isThinking"] = delta.IsThinking,
            ["messageId"] = messageId,
        }, KautukaJsonContext.Default.JsonObject);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Streaming failures must be converted into terminal Narad error events instead of tearing down the response.")]
    private async Task<AgentRunExecutionSummary> ProduceAgentStreamAsync(
        ChannelWriter<NaradPersistedEvent> writer,
        AgentRuntime runtime, string agentId, string sessionId, string message,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        ToolApprovalResponseInfo? approvalResponse,
        AgentContinuationRequest? continuationRequest,
        string runId,
        long runStartTicks,
        DateTimeOffset runStartedAt,
        CancellationToken ct,
        string? userId = null,
        NaradEventContext? runtimeEventContext = null,
        AssistantMessageLifecycle? messageLifecycle = null,
        Activity? runActivity = null)
    {
        AgentRunHostCallTracer.RestoreCurrent(runActivity);
        CancellationToken callerCt = ct;
        ResolvedRunLimits limits = runLimitResolver?.ResolveForAgent(agentId)
            ?? new ResolvedRunLimits(TimeSpan.FromSeconds(300), null, null, HitlExpiryAction.Abort);
        using ActiveRunBudget runBudget = ActiveRunBudget.Start(
            limits.MaxRunDuration,
            continuationRequest?.ActiveElapsed ?? TimeSpan.Zero,
            ct);
        ct = runBudget.Token;

        var eventWriter = CreateExecutionEventWriter();
        messageLifecycle ??= new AssistantMessageLifecycle(
            BuildAssistantMessageId(
                runtimeEventContext?.RootRunId ?? runId,
                runId,
                continuationRequest?.ContinuationRunId ?? runId));
        var fullText = new StringBuilder();
        var fullThinking = new StringBuilder();
        var completedToolCalls = continuationRequest?.PriorToolCalls is { Count: > 0 } priorToolCalls
            ? [.. priorToolCalls]
            : new List<GatewayToolCall>();
        bool suppressToolFailureAssistantOutput = !ChatExecutionHelpers.CanEmitAssistantOutput(completedToolCalls);
        var generatedAssets = new List<GatewayConversationAsset>();
        long tokenIn = 0, tokenOut = 0;
        var usageTurns = new List<GatewayUsageTurn>();
        long startTicks = Stopwatch.GetTimestamp();
        bool hasNativeThinking = false;
        string tagPending = string.Empty;
        bool inTagThought = false;
        string? errorMsg = null;
        bool continuedViaBypass = false; // retained for produce-boundary paths that still check policy auto-approve re-entry
        bool contentProgressSinceCheckpoint = false;
        int approvalRequestCount = 0;
        string? continuationRunId = continuationRequest?.Enabled == true ? continuationRequest.ContinuationRunId : null;
        int nextCheckpointIndex = continuationRequest?.CheckpointIndex is int checkpointIndex ? checkpointIndex + 1 : 0;
        var checkpointGeneratedAssets = new List<GatewayConversationAsset>();
        string result = "completed";
        bool suppressPausedAssistantOutput = false;
        string? activeToolCallId = null;
        string? activeToolName = null;
        Dictionary<string, IDisposable> agentToolCallerScopes = new(StringComparer.Ordinal);
        Dictionary<string, AgentToolCallerContextState> agentToolCallerStates = new(StringComparer.Ordinal);
        GatewayRunState runState = new(GatewayRunPhases.Executing);
        var wireEmittedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        bool hasExistingSessionHistory = approvalResponse is not null
            && await HasExistingSessionHistoryAsync(runtime, ct).ConfigureAwait(false);
        AgentRunHostCallTracer.RestoreCurrent(runActivity);

        async ValueTask EmitToolCallStartAsync(
            string toolCallId,
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            if (!wireEmittedToolCallIds.Add(toolCallId))
            {
                return;
            }

            if (!completedToolCalls.Any(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal)))
            {
                ChatExecutionHelpers.RecordToolCallStart(completedToolCalls, toolCallId, toolName, agentId, argumentsJson);
            }

            await eventWriter.WriteAsync(
                writer,
                new GatewayChatToolCall(
                    toolCallId,
                    toolName,
                    argumentsJson,
                    ActionIdentity.ResolveKind(toolName),
                    ParentToolCallId: AgentToolInvocationContext.Current?.CallerToolCallId,
                    ParentMessageId: messageLifecycle.MessageId),
                runtimeEventContext!,
                messageLifecycle,
                cancellationToken).ConfigureAwait(false);
        }

        async ValueTask PushAgentToolCallerScopeAsync(string toolCallId, string toolName, CancellationToken cancellationToken)
        {
            if (agentToolCallerScopes.Remove(toolCallId, out IDisposable? existingScope))
            {
                existingScope.Dispose();
            }

            string preExecSessionStateJson = await SerializeSessionStateJsonAsync(runtime, cancellationToken).ConfigureAwait(false);
            string preExecBehaviorJson = JsonSerializer.Serialize(runtime.EffectiveBehavior, KautukaJsonContext.Default.EffectiveRunBehavior);

            agentToolCallerScopes[toolCallId] = AgentToolCallerContext.Push(
                runId,
                continuationRunId,
                toolCallId,
                toolName,
                sessionId,
                agentId,
                userId,
                rootRunId: AgentToolInvocationContext.Current?.RootRunId ?? runId,
            preExecSessionStateJson: preExecSessionStateJson,
            preExecBehaviorJson: preExecBehaviorJson);
            agentToolCallerStates[toolCallId] = AgentToolCallerContext.Current
                ?? throw new InvalidOperationException("AAAT caller context was not established.");
        }

        var packedIo = new PackedStreamIo
        {
            EventWriter = eventWriter,
            Writer = writer,
            RuntimeEventContext = runtimeEventContext!,
            MessageLifecycle = messageLifecycle,
            CompletedToolCalls = completedToolCalls,
            Runtime = runtime,
            AgentId = agentId,
            SessionId = sessionId,
            UserId = userId,
            RunId = runId,
            EmitToolCallStart = EmitToolCallStartAsync,
            PushAgentToolCallerScope = PushAgentToolCallerScopeAsync,
            CancellationToken = ct,
            SuppressToolFailureAssistantOutput = suppressToolFailureAssistantOutput,
        };

        if (approvalResponse is not null)
        {
            approvalResponse = PackedToolApproval.AttachPackResponses(
                approvalStore,
                PackedToolApproval.FindActiveWait(
                    GetRunWaitStateStore(),
                    approvalResponse.RequestId,
                    approvalResponse.ContinuationRunId),
                approvalResponse);
            approvalResponse = await JournalPackedToolLifecycleAsync(packedIo, approvalResponse).ConfigureAwait(false);
            suppressToolFailureAssistantOutput = packedIo.SuppressToolFailureAssistantOutput;
        }

        bool resultCompletesExistingToolCall = approvalResponse is { ResultJson.Length: > 0, ToolCallId.Length: > 0 }
            && await HasExistingToolCallInSessionHistoryAsync(runtime, approvalResponse.ToolCallId, ct).ConfigureAwait(false);
        IList<ChatMessage> turnMessages = await BuildTurnMessagesAsync(
            sessionId,
            userId,
            message,
            inputAssets,
            workspaceFiles,
            approvalResponse,
            hasExistingSessionHistory,
            resultCompletesExistingToolCall,
            ct).ConfigureAwait(false);

        try
        {
            bool stopAfterAgentToolApprovalBoundary = false;
            var packedApprovalRequests = new List<ToolApprovalRequestContent>();

            using IDisposable? modelTurnScope = runtimeEventContext is { } turnContext
                ? ModelTurnProgressContext.Begin(
                    GetRuntimeEventPublisher(),
                    turnContext,
                    writer,
                    modelTurnOrdinalCounter)
                : null;
            AgentRunHostCallTracer.RestoreCurrent(runActivity);
            await foreach (AgentResponseUpdate update in NativeApprovalBoundaryStreamDrainer.DrainAfterBoundaryAsync(
                runtime.Agent.RunStreamingAsync(turnMessages, runtime.Session, null, ct),
                () => stopAfterAgentToolApprovalBoundary,
                ct).ConfigureAwait(false))
            {
                AgentRunHostCallTracer.RestoreCurrent(runActivity);
                foreach (AIContent content in update.Contents)
                {
                    if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        if (suppressToolFailureAssistantOutput)
                        {
                            continue;
                        }

                        hasNativeThinking = true;
                        contentProgressSinceCheckpoint = true;
                        fullThinking.Append(reasoning.Text);
                        await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta(reasoning.Text, true), runtimeEventContext!, messageLifecycle, ct).ConfigureAwait(false);
                    }
                    else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        if (suppressToolFailureAssistantOutput)
                        {
                            continue;
                        }

                        if (hasNativeThinking
                            && ChatReasoningEcho.IsNativeReasoningEcho(textContent.Text, fullThinking.ToString()))
                        {
                            continue;
                        }

                        if (hasNativeThinking)
                        {
                            fullText.Append(textContent.Text);
                            await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta(textContent.Text, false), runtimeEventContext!, messageLifecycle, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            var (textPart, thinkPart, newPending, newInThought) =
                                ChatExecutionHelpers.RouteTaggedChunk(tagPending, textContent.Text, inTagThought);
                            tagPending = newPending;
                            inTagThought = newInThought;
                            if (!string.IsNullOrEmpty(thinkPart))
                            {
                                contentProgressSinceCheckpoint = true;
                                fullThinking.Append(thinkPart);
                                await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta(thinkPart, true), runtimeEventContext!, messageLifecycle, ct).ConfigureAwait(false);
                            }
                            if (!string.IsNullOrEmpty(textPart))
                            {
                                contentProgressSinceCheckpoint = true;
                                fullText.Append(textPart);
                                await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta(textPart, false), runtimeEventContext!, messageLifecycle, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    else if (content is FunctionCallContent fnCall)
                    {
                        string argsJson = fnCall.Arguments is not null
                            ? JsonSerializer.Serialize(fnCall.Arguments, KautukaJsonContext.Default.Object)
                            : "{}";
                        string callId = fnCall.CallId ?? string.Empty;
                        string toolName = fnCall.Name;
                        activeToolCallId = callId;
                        activeToolName = toolName;

                        // Gate-before-start: do not persist an action until the tool actually runs.
                        // Narad still announces a pending tool from the approval event and closes it
                        // before the interrupt so the workspace can render it safely.
                        PlatformConfiguration fnCallPlatformConfiguration = platformConfigurationProvider.GetCurrent();
                        bool deferToolCallStartForHitl = ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
                            toolExecutionPolicyService,
                            fnCallPlatformConfiguration,
                            agentId,
                            toolName,
                            argsJson);
                        if (!deferToolCallStartForHitl)
                        {
                            ChatExecutionHelpers.RecordToolCallStart(completedToolCalls, callId, toolName, agentId, argsJson);
                        }

                        if (!deferToolCallStartForHitl)
                        {
                            await EmitToolCallStartAsync(callId, toolName, argsJson, ct).ConfigureAwait(false);
                        }

                        if (ActionIdentity.IsAgentToolInvoke(toolName))
                        {
                            await PushAgentToolCallerScopeAsync(callId, toolName, ct).ConfigureAwait(false);
                        }
                    }
                    else if (content is FunctionResultContent fnResult)
                    {
                        AgentToolApprovalSuspension? suspension = null;
                        if (fnResult.CallId is { Length: > 0 } suspendedAgentToolCallId
                            && AgentToolCallerContext.TryTakePendingApproval(
                                runId,
                                suspendedAgentToolCallId,
                                out suspension))
                        {
                            if (agentToolCallerScopes.Remove(suspendedAgentToolCallId, out IDisposable? suspendedAgentToolScope))
                            {
                                suspendedAgentToolScope.Dispose();
                            }
                            agentToolCallerStates.Remove(suspendedAgentToolCallId);

                            await PauseParentForNestedApprovalAsync(
                                sessionId,
                                agentId,
                                userId,
                                runId,
                                continuationRunId,
                                suspension!.ApprovalRequest,
                                suspension.CallerContext,
                                checkpointGeneratedAssets,
                                runActivity).ConfigureAwait(false);
                            result = "waiting_on_child";
                            suppressPausedAssistantOutput = true;
                            activeToolCallId = null;
                            activeToolName = null;
                            continue;
                        }

                        string toolName = ResolveToolName(completedToolCalls, fnResult.CallId);
                        if (ActionIdentity.IsAgentToolInvoke(toolName)
                            && fnResult.CallId is { Length: > 0 } durableSuspendedAgentToolCallId
                            && TryResolveDurableNestedApprovalSuspension(
                                sessionId,
                                durableSuspendedAgentToolCallId,
                                agentToolCallerStates,
                                out suspension))
                        {
                            if (agentToolCallerScopes.Remove(durableSuspendedAgentToolCallId, out IDisposable? suspendedScope))
                            {
                                suspendedScope.Dispose();
                            }
                            agentToolCallerStates.Remove(durableSuspendedAgentToolCallId);

                            await PauseParentForNestedApprovalAsync(
                                sessionId,
                                agentId,
                                userId,
                                runId,
                                continuationRunId,
                                suspension!.ApprovalRequest,
                                suspension.CallerContext,
                                checkpointGeneratedAssets,
                                runActivity).ConfigureAwait(false);
                            result = "waiting_on_child";
                            suppressPausedAssistantOutput = true;
                            activeToolCallId = null;
                            activeToolName = null;
                            continue;
                        }

                        if (ActionIdentity.IsAgentToolInvoke(toolName)
                            && fnResult.CallId is { Length: > 0 } completedAgentToolCallId
                            && agentToolCallerScopes.Remove(completedAgentToolCallId, out IDisposable? completedAgentToolScope))
                        {
                            completedAgentToolScope.Dispose();
                            agentToolCallerStates.Remove(completedAgentToolCallId);
                        }

                        string resultCallId = fnResult.CallId ?? string.Empty;
                        bool hasOpenToolCall = completedToolCalls.Any(call =>
                            string.Equals(call.ToolCallId, resultCallId, StringComparison.Ordinal)
                            && string.Equals(call.Status, "calling", StringComparison.Ordinal));
                        if (!hasOpenToolCall)
                        {
                            AgentChatServiceLog.OrphanedFunctionResultIgnored(
                                logger,
                                string.IsNullOrWhiteSpace(resultCallId) ? "(missing)" : resultCallId);
                            continue;
                        }

                        if (ChatExecutionHelpers.TryGetFunctionFailure(fnResult, out Exception? toolException))
                        {
                            GatewayChatToolFailure toolFailure = GatewayChatToolFailure.ExecutionFailed(resultCallId, toolName);
                            string failureJson = JsonSerializer.Serialize(
                                toolFailure,
                                KautukaJsonContext.Default.GatewayChatToolFailure);

                            ChatExecutionHelpers.RecordToolCallFailure(completedToolCalls, resultCallId, failureJson);
                            suppressToolFailureAssistantOutput = true;
                            tagPending = string.Empty;
                            inTagThought = false;
                            activeToolCallId = null;
                            activeToolName = null;
                            AgentChatServiceLog.FrameworkToolExecutionFailed(
                                logger,
                                agentId,
                                sessionId,
                                runId,
                                resultCallId,
                                string.IsNullOrWhiteSpace(toolName) ? "(unknown)" : toolName,
                                toolFailure.Code,
                                toolException!.GetType().FullName ?? toolException.GetType().Name);

                            await eventWriter.WriteAsync(
                                writer,
                                toolFailure,
                                runtimeEventContext!,
                                messageLifecycle,
                                ct).ConfigureAwait(false);
                            continue;
                        }

                        string? workspaceRoot = null;
                        if (workspaceTool is not null && !string.IsNullOrWhiteSpace(agentId))
                        {
                            try
                            {
                                workspaceRoot = workspaceTool.DescribeAgentWorkspace(agentId, userId).HostWorkspacePath;
                            }
                            catch (InvalidOperationException)
                            {
                                workspaceRoot = null;
                            }
                        }

                        ChatToolResultProcessor.ProcessedToolResult processedResult = ChatToolResultProcessor.ProcessToolResult(fnResult.Result, toolName, workspaceRoot);

                        string? resultJson = processedResult.ResultJson;
                        if (processedResult.Assets.Count > 0)
                        {
                            IReadOnlyList<GatewayConversationAsset> newAssets = await CreateAssistantAssetsAsync(
                                sessionId,
                                userId,
                                processedResult.Assets,
                                ct).ConfigureAwait(false);
                            if (newAssets.Count > 0)
                            {
                                generatedAssets.AddRange(newAssets);
                                checkpointGeneratedAssets.AddRange(newAssets);
                            }
                        }

                        ChatExecutionHelpers.RecordToolCallResult(completedToolCalls, resultCallId, resultJson);
                        suppressToolFailureAssistantOutput = !ChatExecutionHelpers.CanEmitAssistantOutput(completedToolCalls);
                        activeToolCallId = null;
                        activeToolName = null;

                        await eventWriter.WriteAsync(
                            writer,
                            new GatewayChatToolResult(resultCallId, resultJson),
                            runtimeEventContext!,
                            messageLifecycle,
                            ct).ConfigureAwait(false);
                        if (processedResult.View is not null)
                        {
                            await eventWriter.WriteAsync(
                                writer,
                                new GatewayChatViewOpened(resultCallId, processedResult.View.ToJsonString()),
                                runtimeEventContext!,
                                messageLifecycle,
                                ct).ConfigureAwait(false);
                        }

                        IssuedCheckpointedContinuationResult? toolCheckpoint = await TryCreateCheckpointAsync(
                            runtime,
                            sessionId,
                            agentId,
                            userId,
                            runId,
                            continuationRunId,
                            ContinuationCheckpointBoundaryTypes.ToolResult,
                            checkpointGeneratedAssets,
                            nextCheckpointIndex,
                            ct).ConfigureAwait(false);
                        if (toolCheckpoint is not null)
                        {
                            nextCheckpointIndex = toolCheckpoint.CheckpointIndex + 1;
                            checkpointGeneratedAssets.Clear();
                            contentProgressSinceCheckpoint = false;
                            // Persist the live runtime session before advertising a resumable checkpoint.
                            await runtimeFactory.SaveSessionAsync(runtime, ct).ConfigureAwait(false);
                            await eventWriter.WriteAsync(
                                writer,
                                new Kautuka.Gateway.Domain.Models.GatewayChatContinuationCheckpoint(
                                    toolCheckpoint.CheckpointId,
                                    toolCheckpoint.ContinuationRunId,
                                    toolCheckpoint.CheckpointIndex,
                                    toolCheckpoint.BoundaryType,
                                    toolCheckpoint.ResumeToken,
                                    toolCheckpoint.ArtifactManifest),
                                runtimeEventContext!,
                                messageLifecycle,
                                ct).ConfigureAwait(false);
                        }
                    }
                    else if (content is ToolApprovalRequestContent tarc)
                    {
                        packedApprovalRequests.Add(tarc);
                    }
                    else if (content is UsageContent usageContent)
                    {
                        int turnIn = (int)(usageContent.Details.InputTokenCount ?? 0);
                        int turnOut = (int)(usageContent.Details.OutputTokenCount ?? 0);
                        if (turnIn > 0 || turnOut > 0)
                        {
                            usageTurns.Add(new GatewayUsageTurn(turnIn, turnOut));
                            tokenIn += turnIn;
                            tokenOut += turnOut;
                        }
                    }
                }
            }

            if (!suppressToolFailureAssistantOutput && !string.IsNullOrEmpty(tagPending))
            {
                if (inTagThought) fullThinking.Append(tagPending);
                else
                {
                    contentProgressSinceCheckpoint = true;
                    fullText.Append(tagPending);
                    await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta(tagPending, false), runtimeEventContext!, messageLifecycle, ct).ConfigureAwait(false);
                }
            }

            await runtimeFactory.SaveSessionAsync(runtime, ct).ConfigureAwait(false);

            if (packedApprovalRequests.Count > 0
                && !string.Equals(result, "waiting_on_child", StringComparison.OrdinalIgnoreCase))
            {
                PackedApprovalFinalizeOutcome packedOutcome = await FinalizePackedApprovalRequestsAsync(
                    packedIo,
                    packedApprovalRequests,
                    continuationRunId,
                    continuationRequest,
                    limits,
                    runBudget,
                    runActivity,
                    inputAssets,
                    workspaceFiles,
                    checkpointGeneratedAssets,
                    runStartTicks,
                    runStartedAt).ConfigureAwait(false);
                approvalRequestCount += packedOutcome.HitlRequestCount;
                suppressToolFailureAssistantOutput = packedIo.SuppressToolFailureAssistantOutput;
                if (packedOutcome.PauseForHitl)
                {
                    result = "pending_approval";
                    suppressPausedAssistantOutput = true;
                }
                else if (packedOutcome.AutoContinuation is not null)
                {
                    continuedViaBypass = true;
                    return packedOutcome.AutoContinuation;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(activeToolCallId)
                && wireEmittedToolCallIds.Contains(activeToolCallId))
            {
                await eventWriter.WriteRuntimeAsync(
                    writer,
                    NaradEventTypes.ToolCallFinished,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["toolCallId"] = activeToolCallId,
                        ["toolName"] = activeToolName,
                        ["response"] = null,
                        ["reason"] = "cancelled",
                        ["outcome"] = "cancelled",
                    }, KautukaJsonContext.Default.JsonObject),
                    runtimeEventContext!,
                    CancellationToken.None).ConfigureAwait(false);
            }
            result = GetRunStopRegistry()?.IsStopped(runId) == true
                ? "stopped"
                : !callerCt.IsCancellationRequested
                    ? "timed_out"
                    : "aborted";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(activeToolCallId)
                && wireEmittedToolCallIds.Contains(activeToolCallId))
            {
                await eventWriter.WriteRuntimeAsync(
                    writer,
                    NaradEventTypes.ToolCallFinished,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["toolCallId"] = activeToolCallId,
                        ["toolName"] = activeToolName,
                        ["response"] = null,
                        ["message"] = ex.Message,
                        ["outcome"] = "error",
                    }, KautukaJsonContext.Default.JsonObject),
                    runtimeEventContext!,
                    CancellationToken.None).ConfigureAwait(false);
            }
            result = "failed";
            errorMsg = ex.Message;
            AgentChatServiceLog.AgentStreamFailed(logger, ex, agentId);
        }
        finally
        {
            try
            {
                if (!continuedViaBypass)
                {
                    if (errorMsg is not null)
                    {
                        await eventWriter.WriteAsync(writer, new GatewayChatTokenDelta($"[Error: {errorMsg}]", false), runtimeEventContext!, messageLifecycle, CancellationToken.None).ConfigureAwait(false);
                        await eventWriter.WriteAsync(writer, new GatewayChatStreamDone(Error("agent", agentId, sessionId, errorMsg)), runtimeEventContext!, messageLifecycle, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        bool unresolvedToolFailure = string.Equals(result, "completed", StringComparison.Ordinal)
                            && suppressToolFailureAssistantOutput;
                        if (unresolvedToolFailure)
                        {
                            result = "failed";
                        }
                        bool suppressTerminalAssistantOutput = suppressPausedAssistantOutput || unresolvedToolFailure;
                        string responseText = suppressTerminalAssistantOutput ? string.Empty : fullText.ToString().Trim();
                        string thinkingText = suppressTerminalAssistantOutput ? string.Empty : fullThinking.ToString();
                        AgentChatServiceLog.AaatFinalizingStream(
                            logger,
                            sessionId,
                            agentId,
                            result,
                            suppressPausedAssistantOutput,
                            responseText.Length,
                            thinkingText.Length,
                            completedToolCalls.Count,
                            approvalRequestCount);

                        bool tagBasedThinking = string.IsNullOrEmpty(thinkingText);
                        if (tagBasedThinking)
                            (responseText, thinkingText) = ChatExecutionHelpers.ExtractTaggedThinking(responseText);

                        bool reasoningOnly = string.IsNullOrEmpty(responseText) && !string.IsNullOrEmpty(thinkingText);
                        if (runtime.Capabilities.ReasoningMayBeOnlyOutput && reasoningOnly)
                            (responseText, thinkingText) = (thinkingText, string.Empty);

                        RuntimeObservation obs = reasoningOnly ? RuntimeObservation.ReasoningWithEmptyText : RuntimeObservation.StreamingSuccess;
                        await capabilityService.RecordObservationAsync(runtime.CapabilityProfile.Key, obs, CancellationToken.None).ConfigureAwait(false);
                        if (reasoningOnly && tagBasedThinking)
                            await observationStore.RecordReasoningOnlyAsync(runtime.PathKey, CancellationToken.None).ConfigureAwait(false);

                        long elapsedMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                        AgentChatServiceLog.AgentStreamCompleted(logger, agentId, tokenIn, tokenOut, elapsedMs);

                        IssuedCheckpointedContinuationResult? completedCheckpoint = suppressTerminalAssistantOutput
                            || string.Equals(result, "aborted", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : await TryCreateCheckpointAsync(
                                runtime,
                                sessionId,
                                agentId,
                                userId,
                                runId,
                                continuationRunId,
                                ContinuationCheckpointBoundaryTypes.AssistantTurnCompleted,
                                checkpointGeneratedAssets,
                                nextCheckpointIndex,
                                CancellationToken.None,
                                force: contentProgressSinceCheckpoint).ConfigureAwait(false);
                        if (completedCheckpoint is not null)
                        {
                            await eventWriter.WriteAsync(
                                writer,
                                new Kautuka.Gateway.Domain.Models.GatewayChatContinuationCheckpoint(
                                    completedCheckpoint.CheckpointId,
                                    completedCheckpoint.ContinuationRunId,
                                    completedCheckpoint.CheckpointIndex,
                                    completedCheckpoint.BoundaryType,
                                    completedCheckpoint.ResumeToken,
                                    completedCheckpoint.ArtifactManifest),
                                runtimeEventContext!,
                                messageLifecycle,
                                CancellationToken.None).ConfigureAwait(false);
                        }

                        // Nested AAAT: durable wait is in PauseParentForNestedApprovalAsync.
                        // Parent keeps running with an open invoke tool — never StreamDone / run.*.
                        if (string.Equals(result, "waiting_on_child", StringComparison.OrdinalIgnoreCase))
                        {
                            await eventWriter.WriteAsync(
                                writer,
                                new GatewayChatProduceBoundarySuspended(RunWaitReasons.Child),
                                runtimeEventContext!,
                                messageLifecycle,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            runState = unresolvedToolFailure
                                ? new GatewayRunState(
                                    GatewayRunPhases.Failed,
                                    ErrorCode: GatewayChatToolFailure.ExecutionFailedCode,
                                    ErrorMessage: "One or more tool executions failed.")
                                : new GatewayRunState(
                                    string.Equals(result, "pending_approval", StringComparison.OrdinalIgnoreCase)
                                    ? GatewayRunPhases.AwaitingToolApproval
                                    : string.Equals(result, "failed", StringComparison.OrdinalIgnoreCase)
                                        ? GatewayRunPhases.Failed
                                        : string.Equals(result, "stopped", StringComparison.OrdinalIgnoreCase)
                                            ? GatewayRunPhases.Stopped
                                            : string.Equals(result, "aborted", StringComparison.OrdinalIgnoreCase)
                                                ? GatewayRunPhases.Aborted
                                            : GatewayRunPhases.Completed);
                            await eventWriter.WriteAsync(
                                writer,
                                new GatewayChatStreamDone(new GatewayChatResult("agent", agentId, sessionId, responseText, false,
                                    new GatewayMetrics((int)tokenIn, (int)tokenOut, (int)elapsedMs),
                                    completedToolCalls,
                                    string.IsNullOrWhiteSpace(thinkingText) ? null : thinkingText,
                                    generatedAssets.Count > 0 ? generatedAssets : null,
                                    runtime.EffectiveBehavior,
                                    GetUsageCaptureAccessor().Snapshot(),
                                    runActivity?.TraceId.ToString(),
                                    runState,
                                    UsageTurns: usageTurns.Count > 0 ? usageTurns : null)),
                                runtimeEventContext!,
                                messageLifecycle,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                foreach (IDisposable scope in agentToolCallerScopes.Values)
                {
                    scope.Dispose();
                }

                agentToolCallerScopes.Clear();
                agentToolCallerStates.Clear();
                writer.TryComplete();
            }
        }

        GatewayCapturedUsageMetrics? usage = GetUsageCaptureAccessor().Snapshot();
        return new AgentRunExecutionSummary(
            result,
            (long)Stopwatch.GetElapsedTime(runStartTicks).TotalMilliseconds,
            tokenIn,
            tokenOut,
            completedToolCalls.Count,
            approvalRequestCount,
            usage,
            ResolveFirstModelOutputLatencyMs(usage, runStartedAt));
    }

    private static int? ResolveFirstModelOutputLatencyMs(GatewayCapturedUsageMetrics? usage, DateTimeOffset runStartedAt)
    {
        if (usage?.FirstModelOutputAt is not { Length: > 0 } firstModelOutputAt
            || !DateTimeOffset.TryParse(firstModelOutputAt, out DateTimeOffset parsedFirstModelOutputAt))
        {
            return null;
        }

        return Math.Max(0, (int)(parsedFirstModelOutputAt - runStartedAt).TotalMilliseconds);
    }

    private sealed record AgentRunExecutionSummary(
        string Result,
        long DurationMs,
        long TokenIn,
        long TokenOut,
        int ToolCallCount,
        int ApprovalRequestCount,
        GatewayCapturedUsageMetrics? Usage,
        int? FirstModelOutputLatencyMs);

    private static string ResolveToolName(List<GatewayToolCall> completedToolCalls, string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return string.Empty;
        }

        for (int index = completedToolCalls.Count - 1; index >= 0; index--)
        {
            if (string.Equals(completedToolCalls[index].ToolCallId, toolCallId, StringComparison.Ordinal))
            {
                return completedToolCalls[index].ToolName;
            }
        }

        return string.Empty;
    }

    private async Task<IssuedCheckpointedContinuationResult?> TryCreateCheckpointAsync(
        AgentRuntime runtime,
        string sessionId,
        string agentId,
        string? userId,
        string runId,
        string? continuationRunId,
        string boundaryType,
        IReadOnlyList<GatewayConversationAsset> artifactAssets,
        int checkpointIndex,
        CancellationToken cancellationToken,
        bool force = true,
        string? overrideSessionJson = null)
    {
        if (!force || string.IsNullOrWhiteSpace(continuationRunId))
        {
            return null;
        }

        string? sessionStateJson = overrideSessionJson;
        if (sessionStateJson is null)
        {
            sessionStateJson = await SerializeSessionStateJsonAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        List<ContinuationArtifactManifestEntry> artifactManifest = [..
            artifactAssets.Select(static asset => new ContinuationArtifactManifestEntry(asset.Id, asset.FileName, asset.ContentType))];

        return await GetResumeTokenProvider().IssueCheckpointAsync(
            new ContinuationCheckpointCreateRequest(
                continuationRunId,
                sessionId,
                agentId,
                userId,
                runId,
                boundaryType,
                sessionStateJson,
                JsonSerializer.Serialize(runtime.EffectiveBehavior, KautukaJsonContext.Default.EffectiveRunBehavior),
                artifactManifest,
                checkpointIndex),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildArtifactManifestJson(IReadOnlyList<GatewayConversationAsset> artifactAssets)
        => JsonSerializer.Serialize([
            .. artifactAssets.Select(static asset => new ContinuationArtifactManifestEntry(asset.Id, asset.FileName, asset.ContentType)),
        ], KautukaJsonContext.Default.ListContinuationArtifactManifestEntry);

    private async ValueTask PauseParentForNestedApprovalAsync(
        string sessionId,
        string agentId,
        string? userId,
        string runId,
        string? continuationRunId,
        GatewayChatApprovalRequest approvalRequest,
        AgentToolCallerContextState callerContext,
        IReadOnlyList<GatewayConversationAsset> checkpointGeneratedAssets,
        Activity? runActivity)
    {
        string toolCallId = callerContext.CallerToolCallId;
        string toolName = callerContext.CallerToolName;
        if (string.IsNullOrWhiteSpace(continuationRunId))
        {
            throw new InvalidOperationException($"AAAT approval '{approvalRequest.RequestId}' has no parent continuation run.");
        }

        IAgentToolInvocationLinkStore invocationLinkStore = GetAgentToolInvocationLinkStore();
        GatewayToolApprovalRequest? storedApproval = approvalStore.GetById(approvalRequest.RequestId);
        AgentToolInvocationLink? link = invocationLinkStore.GetByApprovalRequestId(approvalRequest.RequestId);
        string? resolvedChildAgentId = approvalRequest.AgentId ?? storedApproval?.AgentId ?? link?.ChildAgentId;
        string? resolvedChildRunId = approvalRequest.RunId ?? storedApproval?.RunId ?? link?.ChildRunId;
        if (link is null
            || string.IsNullOrWhiteSpace(link.ChildSessionId)
            || !string.Equals(link.CallerToolCallId, toolCallId, StringComparison.Ordinal)
            || !string.Equals(link.CallerSessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(link.CallerRunId, callerContext.CallerRunId, StringComparison.Ordinal)
            || !string.Equals(link.ChildRunId, resolvedChildRunId, StringComparison.Ordinal)
            || !string.Equals(link.ChildAgentId, resolvedChildAgentId, StringComparison.Ordinal)
            || !string.Equals(link.RootRunId, callerContext.RootRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"AAAT approval '{approvalRequest.RequestId}' has incomplete parent/child lineage.");
        }

        string sessionStateJson = callerContext.PreExecSessionStateJson
            ?? throw new InvalidOperationException("AAAT parent continuation is missing the pre-execution session snapshot.");
        string behaviorJson = callerContext.PreExecBehaviorJson
            ?? throw new InvalidOperationException("AAAT parent continuation is missing the pre-execution behavior snapshot.");

        GetRunWaitStateStore().PauseRunForChild(new RunWaitStateCreateRequest(
            continuationRunId,
            runId,
            "agent",
            agentId,
            sessionId,
            agentId,
            userId ?? callerContext?.UserId,
            CorrelationId: link.ChildSessionId,
            sessionStateJson,
            behaviorJson,
            BuildArtifactManifestJson(checkpointGeneratedAssets),
            ToolCallId: toolCallId,
            ToolName: toolName,
            ApprovalRequestId: link.ApprovalRequestId,
            ChildSessionId: link.ChildSessionId,
            ParentRunId: runId,
            RootRunId: link.RootRunId,
            ChildRunId: link.ChildRunId,
            CanonicalEventId: link.CanonicalEventId,
            TraceContext: AgentRunHostCallTracer.CaptureRunTraceContext(runActivity, runId)));
    }

    private bool TryResolveDurableNestedApprovalSuspension(
        string callerSessionId,
        string callerToolCallId,
        IReadOnlyDictionary<string, AgentToolCallerContextState> activeCallerContexts,
        out AgentToolApprovalSuspension? suspension)
    {
        suspension = null;
        AgentToolInvocationLink? link = GetAgentToolInvocationLinkStore()
            .GetByCallerSessionAndToolCallId(callerSessionId, callerToolCallId);
        if (link is null
            || approvalStore.GetById(link.ApprovalRequestId) is not { Decision: null } storedApproval
            || (activeCallerContexts.GetValueOrDefault(callerToolCallId)
                ?? AgentToolCallerContext.TryGetRegistered(callerSessionId, callerToolCallId)) is not { } callerContext)
        {
            return false;
        }

        // A detached approval continuation can use a new delivery run id. First recover
        // the original typed suspension by its durable caller identity. If process-local
        // state is gone, reconstruct it from the exact child approval wait.
        if (AgentToolCallerContext.TryTakePendingApproval(
                link.CallerRunId ?? callerContext.CallerRunId,
                callerToolCallId,
                out AgentToolApprovalSuspension? registeredSuspension)
            && string.Equals(
                registeredSuspension?.ApprovalRequest.RequestId,
                storedApproval.Id,
                StringComparison.Ordinal))
        {
            suspension = new AgentToolApprovalSuspension(registeredSuspension!.ApprovalRequest, callerContext);
            return true;
        }

        RunWaitState? childApprovalWait = GetRunWaitStateStore().ListRecoverable()
            .FirstOrDefault(wait => string.Equals(wait.WaitReason, RunWaitReasons.Approval, StringComparison.Ordinal)
                && string.Equals(wait.ApprovalRequestId, storedApproval.Id, StringComparison.Ordinal)
                && string.Equals(wait.SessionId, link.ChildSessionId, StringComparison.Ordinal));
        if (childApprovalWait is null)
        {
            return false;
        }

        var approvalRequest = new GatewayChatApprovalRequest(
            storedApproval.Id,
            storedApproval.ToolCallId,
            storedApproval.ToolName,
            storedApproval.ArgumentsJson,
            storedApproval.RunId,
            storedApproval.CheckpointId,
            storedApproval.McpServerId,
            storedApproval.ToolType,
            childApprovalWait.ContinuationRunId,
            storedApproval.OuterSessionId,
            storedApproval.OuterAgentId,
            AgentId: storedApproval.AgentId,
            SessionId: storedApproval.SessionId);
        suspension = new AgentToolApprovalSuspension(approvalRequest, callerContext);
        return true;
    }

    private static bool IsSameRunAutoApprovalContinuation(string? reason)
        => string.Equals(reason, "policy:auto", StringComparison.Ordinal)
            || string.Equals(reason, "bypass:auto", StringComparison.Ordinal);

    private async Task<IReadOnlyList<GatewayConversationAsset>> CreateAssistantAssetsAsync(
        string sessionId,
        string? userId,
        IReadOnlyList<ChatToolResultProcessor.PendingConversationAsset> pendingAssets,
        CancellationToken cancellationToken)
    {
        var createdAssets = new List<GatewayConversationAsset>(pendingAssets.Count);

        foreach (ChatToolResultProcessor.PendingConversationAsset pendingAsset in pendingAssets)
        {
            try
            {
                using var content = new MemoryStream(pendingAsset.ContentBytes, writable: false);
                GatewayConversationAsset createdAsset = await assetStore.CreateUploadAsync(
                    sessionId,
                    userId,
                    new AssetUploadCommand(
                        pendingAsset.FileName,
                        pendingAsset.ContentType,
                        pendingAsset.ContentBytes.Length,
                        "agentOutput",
                        pendingAsset.Disposition,
                        content),
                    cancellationToken).ConfigureAwait(false);
                createdAssets.Add(createdAsset);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                AgentChatServiceLog.AgentOutputAssetPersistFailed(logger, ex, sessionId, pendingAsset.FileName);
            }
        }

        return createdAssets;
    }

}
