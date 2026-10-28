using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Observability;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Agents.AgentTools;

internal sealed class AgentToolInvocationOrchestrator(
    Lazy<IChatGatewayService> chatGatewayServiceLazy,
    ISessionLifecycleService sessionLifecycleService,
    IAgentToolInvocationStore invocationStore,
    IGatewayApprovalStore approvalStore,
    IAgentToolApprovalWaiter approvalWaiter,
    ILogger<AgentToolInvocationOrchestrator> logger,
    IRuntimeEventPublisher runtimeEventPublisher,
    IRunWaitStateStore waitStateStore) : IAgentToolInvocationOrchestrator
{
    private const string agentToolRunTrigger = "agent_tool";

    public async ValueTask<object?> InvokeAsync(
        string callerAgentId,
        string targetAgentId,
        string message,
        string? userId,
        string callerSessionId,
        AgentToolCallerContextState callerContext,
        CancellationToken ct,
        TimeSpan maxRunDuration = default)
    {
        ArgumentNullException.ThrowIfNull(callerContext);
        if (!string.Equals(callerContext.CallerAgentId, callerAgentId, StringComparison.Ordinal)
            || !string.Equals(callerContext.CallerSessionId, callerSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AAAT caller context does not match the invoking agent and session.");
        }

        if (AgentToolInvocationContext.Current is not null)
        {
            throw new InvalidOperationException("Agent-as-a-tool nesting is leaf-only; a child agent cannot invoke another agent tool.");
        }

        GatewaySession session = await sessionLifecycleService.CreateSessionAsync("agent", targetAgentId, userId ?? string.Empty, ct, agentToolRunTrigger).ConfigureAwait(false);
        string sessionId = session.Id;
        string childRunId = Guid.NewGuid().ToString("N");

        AgentToolInvocationOrchestratorLog.StartingInnerAgentRun(
            logger,
            callerAgentId,
            targetAgentId,
            callerSessionId,
            sessionId,
            userId);

        AgentToolRunOutcome initialRun = await ExecuteRunAsync(
            targetAgentId,
            sessionId,
            userId,
            callerAgentId,
            agentToolRunTrigger,
            message,
            callerSessionId,
            callerContext,
            childRunId,
            maxRunDuration,
            ct).ConfigureAwait(false);

        int resultLength = initialRun.Payload switch
        {
            JsonElement payloadElement when payloadElement.ValueKind == JsonValueKind.Object
                && payloadElement.TryGetProperty("result", out JsonElement resultElement)
                && resultElement.ValueKind == JsonValueKind.String
                    => resultElement.GetString()?.Length ?? 0,
            string payloadText => payloadText.Length,
            _ => 0,
        };
        AgentToolInvocationOrchestratorLog.CompletedInnerAgentRun(
            logger,
            targetAgentId,
            sessionId,
            callerSessionId,
            resultLength);

        return initialRun.Payload;
    }

    private async ValueTask<AgentToolRunOutcome> ExecuteRunAsync(
        string targetAgentId,
        string sessionId,
        string? userId,
        string callerAgentId,
        string runTrigger,
        string message,
        string callerSessionId,
        AgentToolCallerContextState callerContext,
        string childRunId,
        TimeSpan maxRunDuration,
        CancellationToken ct)
    {
        using IDisposable invocationScope = AgentToolInvocationContext.Push(
            callerSessionId,
            callerAgentId,
            userId,
            callerContext.CallerRunId,
            callerContext.CallerToolCallId,
            callerContext.RootRunId ?? throw new InvalidOperationException("AAAT caller context has no root run id."));

        JsonObject body = new()
        {
            ["target_type"] = "agent",
            ["target_id"] = targetAgentId,
            ["message"] = message,
            ["session_id"] = sessionId,
            ["source"] = runTrigger,
        };
        body["run_id"] = childRunId;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            body["user_id"] = userId;
        }

        JsonElement streamRequest = JsonDocument.Parse(body.ToJsonString()).RootElement.Clone();
        AgentRequestContext? requestContext = AgentRunHostCallTracer.CaptureCurrentRequestContext();
        StringBuilder output = new();
        GatewayChatResult? finalResult = null;
        string? lastToolResult = null;
        string? terminalEventType = null;
        var childToolOutcomes = new ChildRunToolOutcomeTracker();

        NestedAgentExecutionContext nestedProjection = BuildNestedExecutionContext(
            targetAgentId,
            sessionId,
            userId,
            callerContext);
        NaradEventContext parentEventContext = new(
            callerContext.CallerAgentId,
            callerContext.CallerSessionId,
            callerContext.UserId ?? string.Empty,
            callerContext.CallerRunId,
            callerContext.RootRunId,
            callerContext.CallerRunId,
            callerContext.CallerSessionId,
            callerContext.CallerToolCallId,
            callerContext.CallerContinuationRunId);
        AgentToolInvocation invocation = invocationStore.CreateWaiting(new AgentToolInvocationCreateRequest(
            callerContext.CallerRunId,
            callerContext.CallerSessionId,
            callerContext.CallerToolCallId,
            callerContext.CallerToolName,
            childRunId,
            sessionId,
            targetAgentId,
            callerContext.RootRunId ?? throw new InvalidOperationException("AAAT caller context has no root run id."),
            callerContext.CallerContinuationRunId));

        ToolApprovalResponseInfo? resumeApproval = null;
        bool parkedOnNestedHitl = false;
        TimeSpan accumulatedElapsed = TimeSpan.Zero;
        ActiveRunBudget runBudget = CreateChildRunBudget(maxRunDuration, accumulatedElapsed, ct);
        try
        {
            // Like a normal tool: stay inside this invoke until the child finishes (including nested
            // HITL). Never return pending_approval into the parent FICC/session history.
            // Drain through run.paused after interrupt — breaking on InterruptRequested alone
            // cancels the child producer before the pause boundary is fully emitted.
            // Max-run bounds active child work only. Nested HITL wait uses the caller token.
            // After nested HITL, rebind remaining from the wait-state clock checkpoint so live
            // resume and crash-resume (StreamChatWithApprovalAsync) share one remaining budget.
            while (true)
            {
                runBudget.Resume();
                CancellationToken runningToken = runBudget.Token;
                IAsyncEnumerable<NaradPersistedEvent> stream = resumeApproval is null
                    ? chatGatewayServiceLazy.Value.StreamChatAsync(streamRequest, requestContext, runningToken)
                    : chatGatewayServiceLazy.Value.StreamChatWithApprovalAsync(
                        "agent",
                        targetAgentId,
                        sessionId,
                        resumeApproval,
                        userId,
                        cancellationToken: runningToken);

                runningToken.ThrowIfCancellationRequested();
                GatewayChatApprovalRequest? nestedApproval = null;
                string? nestedApprovalEventId = null;
                await foreach (NaradPersistedEvent runtimeEvent in stream.WithCancellation(runningToken).ConfigureAwait(false))
                {
                    childToolOutcomes.Observe(runtimeEvent);
                    if (string.Equals(runtimeEvent.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
                    {
                        nestedApproval = NaradInterruptObservation.ResolveChatApprovalRequest(
                            runtimeEvent,
                            approvalStore,
                            fallbackAgentId: targetAgentId,
                            fallbackSessionId: sessionId);
                        nestedApprovalEventId = runtimeEvent.EventId;
                        AgentToolInvocationOrchestratorLog.InnerApprovalSuspended(
                            logger,
                            targetAgentId,
                            sessionId,
                            callerSessionId,
                            nestedApproval.RequestId,
                            nestedApproval.ToolCallId,
                            nestedApproval.ToolName);
                        continue;
                    }

                    if (string.Equals(runtimeEvent.EventType, NaradEventTypes.MessageTextDelta, StringComparison.Ordinal))
                    {
                        GatewayChatTokenDelta delta = ReadPayload(runtimeEvent, KautukaJsonContext.Default.GatewayChatTokenDelta);
                        AgentToolInvocationOrchestratorLog.InnerTokenObserved(
                            logger,
                            targetAgentId,
                            sessionId,
                            callerSessionId,
                            delta.Delta);
                        output.Append(delta.Delta);
                        continue;
                    }

                    if (string.Equals(runtimeEvent.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal))
                    {
                        GatewayChatToolResult toolResult = ReadPayload(runtimeEvent, KautukaJsonContext.Default.GatewayChatToolResult);
                        string? resultJson = toolResult.ResultJson;
                        if (string.IsNullOrWhiteSpace(resultJson))
                        {
                            continue;
                        }
                        AgentToolInvocationOrchestratorLog.InnerToolResultObserved(
                            logger,
                            targetAgentId,
                            sessionId,
                            callerSessionId,
                            resultJson);
                        lastToolResult = resultJson;
                        continue;
                    }

                    if (string.Equals(runtimeEvent.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                        || string.Equals(runtimeEvent.EventType, NaradEventTypes.RunError, StringComparison.Ordinal)
                        || string.Equals(runtimeEvent.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
                    {
                        terminalEventType = runtimeEvent.EventType;
                        GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(
                            runtimeEvent,
                            "agent",
                            targetAgentId,
                            sessionId);
                        AgentToolInvocationOrchestratorLog.InnerStreamDoneObserved(
                            logger,
                            targetAgentId,
                            sessionId,
                            callerSessionId,
                            done.Result.Response.Length,
                            done.Result.ToolCalls?.Count ?? 0);
                        finalResult = done.Result;
                        break;
                    }
                }

                if (nestedApproval is null)
                {
                    break;
                }

                EnsureNestedInvocationLink(
                    invocation.InvocationId,
                    nestedProjection,
                    sessionId,
                    nestedApproval,
                    nestedApprovalEventId ?? nestedApproval.RequestId);

                // WaitAsync registers IsWaiting so durable continuation enqueue is skipped.
                // Pause the active budget so HITL wait does not consume max-run.
                TimeSpan capturedElapsed = runBudget.CaptureElapsed(accumulatedElapsed);
                parkedOnNestedHitl = true;
                runBudget.Pause();
                HitlApprovalResolved resolved = await approvalWaiter
                    .WaitAsync(nestedApproval.RequestId, ct)
                    .ConfigureAwait(false);
                parkedOnNestedHitl = false;

                if (resolved.StopRun)
                {
                    string outcome = "stopped";
                    string text = resolved.Source == HitlResolutionSource.SystemTimeout
                        ? "Child run was aborted because the approval expired."
                        : "Child run was stopped.";
                    JsonElement negativePayload = CreateResultPayload(text);
                    invocationStore.RecordChildCompletion(invocation.InvocationId, negativePayload.GetRawText(), outcome);
                    string negativeLeaseOwner = ClaimParentCompletion(invocation.InvocationId);
                    await PublishInvocationCompletedAsync(
                        runtimeEventPublisher,
                        parentEventContext,
                        callerContext,
                        invocation.InvocationId,
                        targetAgentId,
                        sessionId,
                        childRunId,
                        outcome,
                        CancellationToken.None).ConfigureAwait(false);
                    invocationStore.MarkCompleted(invocation.InvocationId, negativeLeaseOwner);
                    return new AgentToolRunOutcome(negativePayload);
                }

                if (!resolved.Approved && resolved.Source != HitlResolutionSource.SystemTimeout)
                {
                    const string outcome = "denied";
                    string text = $"Child tool '{nestedApproval.ToolName}' was denied.";
                    JsonElement negativePayload = CreateResultPayload(text);
                    invocationStore.RecordChildCompletion(invocation.InvocationId, negativePayload.GetRawText(), outcome);
                    string negativeLeaseOwner = ClaimParentCompletion(invocation.InvocationId);
                    await PublishInvocationCompletedAsync(
                        runtimeEventPublisher,
                        parentEventContext,
                        callerContext,
                        invocation.InvocationId,
                        targetAgentId,
                        sessionId,
                        childRunId,
                        outcome,
                        CancellationToken.None).ConfigureAwait(false);
                    invocationStore.MarkCompleted(invocation.InvocationId, negativeLeaseOwner);
                    return new AgentToolRunOutcome(negativePayload);
                }

                accumulatedElapsed = ResolveChildElapsed(nestedApproval, capturedElapsed);
                runBudget.Dispose();
                runBudget = CreateChildRunBudget(maxRunDuration, accumulatedElapsed, ct);

                bool expiredInform = !resolved.Approved && resolved.Source == HitlResolutionSource.SystemTimeout;
                string? expiredJson = expiredInform
                    ? JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["result"] = $"Tool '{nestedApproval.ToolName}' expired without a decision.",
                            ["outcome"] = "expired",
                        },
                        KautukaJsonContext.Default.DictionaryStringString)
                    : null;

                var approvalResponse = new ToolApprovalResponseInfo(
                    nestedApproval.RequestId,
                    nestedApproval.ToolCallId,
                    nestedApproval.ToolName,
                    nestedApproval.ArgumentsJson,
                    Approved: resolved.Approved,
                    Reason: resolved.Reason,
                    RunId: nestedApproval.RunId,
                    CheckpointId: nestedApproval.CheckpointId,
                    ContinuationRunId: nestedApproval.ContinuationRunId,
                    ResultJson: expiredJson,
                    Expired: expiredInform);

                terminalEventType = null;
                finalResult = null;
                resumeApproval = approvalResponse;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller/host cancelled the live waiter. Nested HITL stays waiting_on_child so durable
            // continuation can complete the parent tool after this process dies. Do not invent a
            // failed terminal while parked on an interrupt.
            if (!parkedOnNestedHitl)
            {
                invocationStore.MarkFailed(invocation.InvocationId, "cancelled");
            }

            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            invocationStore.MarkFailed(invocation.InvocationId, "timed_out");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "timed_out", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            invocationStore.MarkFailed(invocation.InvocationId, "timed_out");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "timed_out", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            invocationStore.MarkFailed(invocation.InvocationId, "child_failed");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            runBudget.Dispose();
        }

        string selectedResult = FirstNonEmpty(
            finalResult?.Response,
            output.ToString(),
            finalResult?.ToolCalls?
                .LastOrDefault(static call => !string.IsNullOrWhiteSpace(call.ResultJson))?
                .ResultJson,
            lastToolResult);

        if (string.Equals(terminalEventType, NaradEventTypes.RunError, StringComparison.Ordinal))
        {
            invocationStore.MarkFailed(invocation.InvocationId, "child_failed");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "failed", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Child agent '{targetAgentId}' failed while executing tool '{callerContext.CallerToolName}'.");
        }

        if (string.Equals(terminalEventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
        {
            invocationStore.MarkFailed(invocation.InvocationId, "paused_without_boundary");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "paused_without_boundary", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Child agent '{targetAgentId}' paused without exposing an approval boundary.");
        }

        if (childToolOutcomes.HasUnresolvedFailures)
        {
            invocationStore.MarkFailed(invocation.InvocationId, "child_tool_failed");
            await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "failed", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Child agent '{targetAgentId}' finished with unresolved failed tool outcomes: {childToolOutcomes.DescribeUnresolvedFailures()}.");
        }

        AgentToolInvocationOrchestratorLog.InnerOutcomeSelected(
            logger,
            targetAgentId,
            sessionId,
            callerSessionId,
            finalResult?.Response.Length ?? 0,
            output.Length,
            lastToolResult?.Length ?? 0,
            selectedResult.Length);

        JsonElement resultPayload = CreateResultPayload(selectedResult);
        invocationStore.RecordChildCompletion(invocation.InvocationId, resultPayload.GetRawText(), "completed");
        string completionLeaseOwner = ClaimParentCompletion(invocation.InvocationId);
        await PublishInvocationCompletedAsync(runtimeEventPublisher, parentEventContext, callerContext, invocation.InvocationId, targetAgentId, sessionId, childRunId, "completed", CancellationToken.None).ConfigureAwait(false);
        invocationStore.MarkCompleted(invocation.InvocationId, completionLeaseOwner);
        return new AgentToolRunOutcome(resultPayload);
    }

    private static ActiveRunBudget CreateChildRunBudget(
        TimeSpan maxRunDuration,
        TimeSpan alreadyElapsed,
        CancellationToken ct)
        => ActiveRunBudget.Start(
            maxRunDuration > TimeSpan.Zero ? maxRunDuration : null,
            alreadyElapsed,
            ct);

    /// <summary>
    /// Prefer the wait-state checkpoint when present so remaining max-run matches crash-resume.
    /// Take the larger elapsed so in-process drift cannot grant extra active time.
    /// </summary>
    private TimeSpan ResolveChildElapsed(GatewayChatApprovalRequest nestedApproval, TimeSpan capturedElapsed)
    {
        TimeSpan elapsed = capturedElapsed;
        RunWaitState? wait = null;
        if (!string.IsNullOrWhiteSpace(nestedApproval.ContinuationRunId))
        {
            wait = waitStateStore.GetActiveByContinuationRunId(nestedApproval.ContinuationRunId);
        }

        wait ??= waitStateStore.GetActiveByCorrelationId(nestedApproval.RequestId);
        if (wait is not null
            && RunClockEnvelope.TryReadElapsed(wait.BehaviorJson, out TimeSpan durableElapsed)
            && durableElapsed > elapsed)
        {
            elapsed = durableElapsed;
        }

        return elapsed;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate)) ?? string.Empty;

    private static ValueTask PublishInvocationCompletedAsync(
        IRuntimeEventPublisher? publisher,
        NaradEventContext parentContext,
        AgentToolCallerContextState caller,
        string invocationId,
        string childAgentId,
        string childSessionId,
        string childRunId,
        string outcome,
        CancellationToken ct)
    {
        _ = publisher;
        _ = parentContext;
        _ = caller;
        _ = invocationId;
        _ = childAgentId;
        _ = childSessionId;
        _ = childRunId;
        _ = outcome;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static NestedAgentExecutionContext BuildNestedExecutionContext(
        string targetAgentId,
        string childSessionId,
        string? userId,
        AgentToolCallerContextState caller)
    {
        return new NestedAgentExecutionContext(
            targetAgentId,
            childSessionId,
            caller.CallerToolCallId,
            caller.CallerToolName,
            caller.CallerSessionId,
            caller.CallerContinuationRunId,
            userId ?? caller.UserId ?? string.Empty,
            caller.CallerRunId,
            caller.RootRunId ?? throw new InvalidOperationException("AAAT caller context has no root run id."));
    }

    private void EnsureNestedInvocationLink(
        string invocationId,
        NestedAgentExecutionContext nestedProjection,
        string childSessionId,
        GatewayChatApprovalRequest approvalRequest,
        string canonicalEventId)
    {
        string callerToolCallId = nestedProjection.CallerToolCallId;
        string callerToolName = nestedProjection.CallerToolName;
        string? callerContinuationRunId = nestedProjection.CallerContinuationRunId;
        string resolvedCallerSessionId = nestedProjection.CallerSessionId;

        string timestamp = DateTimeOffset.UtcNow.ToString("O");
        invocationStore.RecordApprovalLink(new AgentToolInvocationLink(
            approvalRequest.RequestId,
            resolvedCallerSessionId,
            timestamp,
            timestamp,
            CallerToolCallId: callerToolCallId,
            CallerToolName: callerToolName,
            CallerContinuationRunId: callerContinuationRunId,
            ChildSessionId: childSessionId,
            CallerRunId: nestedProjection.CallerRunId,
            ChildRunId: approvalRequest.RunId,
            ChildAgentId: nestedProjection.TargetAgentId,
            RootRunId: nestedProjection.RootRunId,
            CanonicalEventId: canonicalEventId,
            InvocationId: invocationId));
    }

    private string ClaimParentCompletion(string invocationId)
    {
        string leaseOwner = $"inline:{Environment.ProcessId}:{Guid.NewGuid():N}";
        // Soft mutex only; seal is idempotent via MarkCompleted/TrySealCompleted after the journal advances.
        if (!invocationStore.TryClaimParentCompletion(invocationId, leaseOwner, TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException($"AAAT invocation '{invocationId}' was already completed or claimed.");
        }

        return leaseOwner;
    }

    private static T ReadPayload<T>(NaradPersistedEvent persistedEvent, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(NaradPersistedEventReader.ParseBody(persistedEvent), typeInfo)
            ?? throw new InvalidOperationException($"Authority event '{persistedEvent.EventId}' has an empty {typeof(T).Name} payload.");

    private static JsonElement CreateResultPayload(string result)
    {
        JsonObject payload = new()
        {
            ["result"] = result,
        };

        return JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone();
    }

    private sealed record AgentToolRunOutcome(object? Payload);
}

internal static partial class AgentToolInvocationOrchestratorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting inner agent run for target agent {TargetAgentId} from caller agent {CallerAgentId}; callerSessionId={CallerSessionId}, innerSessionId={InnerSessionId}, userId={UserId}")]
    private static partial void StartingInnerAgentRunCore(ILogger logger, string callerAgentId, string targetAgentId, string callerSessionId, string innerSessionId, string? userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Completed inner agent run for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, resultLength={ResultLength}")]
    private static partial void CompletedInnerAgentRunCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int resultLength);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Inner agent approval suspended for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, requestId={RequestId}, toolCallId={ToolCallId}, toolName={ToolName}")]
    private static partial void InnerApprovalSuspendedCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, string requestId, string toolCallId, string toolName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Observed inner agent token for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, deltaLength={DeltaLength}")]
    private static partial void InnerTokenObservedCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int deltaLength);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Observed inner agent tool result for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, resultLength={ResultLength}")]
    private static partial void InnerToolResultObservedCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int resultLength);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Observed inner agent stream completion for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, responseLength={ResponseLength}, toolCallCount={ToolCallCount}")]
    private static partial void InnerStreamDoneObservedCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int responseLength, int toolCallCount);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Selected inner agent outcome for target agent {TargetAgentId}; innerSessionId={InnerSessionId}, callerSessionId={CallerSessionId}, finalResponseLength={FinalResponseLength}, tokenOutputLength={TokenOutputLength}, lastToolResultLength={LastToolResultLength}, selectedResultLength={SelectedResultLength}")]
    private static partial void InnerOutcomeSelectedCore(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int finalResponseLength, int tokenOutputLength, int lastToolResultLength, int selectedResultLength);

    public static void StartingInnerAgentRun(ILogger logger, string callerAgentId, string targetAgentId, string callerSessionId, string innerSessionId, string? userId)
    {
        StartingInnerAgentRunCore(logger, callerAgentId, targetAgentId, callerSessionId, innerSessionId, userId);
    }

    public static void CompletedInnerAgentRun(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int resultLength)
    {
        CompletedInnerAgentRunCore(logger, targetAgentId, innerSessionId, callerSessionId, resultLength);
    }

    public static void InnerApprovalSuspended(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, string requestId, string toolCallId, string toolName)
    {
        InnerApprovalSuspendedCore(logger, targetAgentId, innerSessionId, callerSessionId, requestId, toolCallId, toolName);
    }

    public static void InnerTokenObserved(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, string delta)
    {
        InnerTokenObservedCore(logger, targetAgentId, innerSessionId, callerSessionId, delta.Length);
    }

    public static void InnerToolResultObserved(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, string resultJson)
    {
        InnerToolResultObservedCore(logger, targetAgentId, innerSessionId, callerSessionId, resultJson.Length);
    }

    public static void InnerStreamDoneObserved(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int responseLength, int toolCallCount)
    {
        InnerStreamDoneObservedCore(logger, targetAgentId, innerSessionId, callerSessionId, responseLength, toolCallCount);
    }

    public static void InnerOutcomeSelected(ILogger logger, string targetAgentId, string innerSessionId, string callerSessionId, int finalResponseLength, int tokenOutputLength, int lastToolResultLength, int selectedResultLength)
    {
        InnerOutcomeSelectedCore(logger, targetAgentId, innerSessionId, callerSessionId, finalResponseLength, tokenOutputLength, lastToolResultLength, selectedResultLength);
    }

}
