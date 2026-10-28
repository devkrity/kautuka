using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

internal static class NaradTestEventFactory
{
    public static NaradPersistedEvent FromChatEvent(
        GatewayChatStreamEvent chatEvent,
        string sessionId = "session-test",
        string agentId = "agent-test",
        string runId = "run-test",
        long sessionSequence = 1,
        long rootSequence = 1)
    {
        string eventType;
        JsonElement body;
        switch (chatEvent)
        {
            case GatewayChatSessionReady ready:
                eventType = NaradEventTypes.RunStarted;
                body = JsonSerializer.SerializeToElement(ready, KautukaJsonContext.Default.GatewayChatSessionReady);
                sessionId = ready.SessionId;
                break;
            case GatewayChatTokenDelta token:
                eventType = token.IsThinking ? NaradEventTypes.ReasoningDelta : NaradEventTypes.MessageTextDelta;
                body = JsonSerializer.SerializeToElement(token, KautukaJsonContext.Default.GatewayChatTokenDelta);
                break;
            case GatewayChatToolCall toolCall:
                eventType = NaradEventTypes.ToolCallStarted;
                body = JsonSerializer.SerializeToElement(toolCall, KautukaJsonContext.Default.GatewayChatToolCall);
                break;
            case GatewayChatToolResult toolResult:
                eventType = NaradEventTypes.ToolCallFinished;
                {
                    JsonObject finishedBody = JsonSerializer.SerializeToNode(toolResult, KautukaJsonContext.Default.GatewayChatToolResult)!
                        .AsObject();
                    finishedBody["outcome"] = "success";
                    body = JsonSerializer.SerializeToElement(finishedBody, KautukaJsonContext.Default.JsonObject);
                }
                break;
            case GatewayChatApprovalRequest approval:
                eventType = NaradEventTypes.InterruptRequested;
                body = Narad.Contracts.NaradEventBodies.InterruptRequested(
                    approval.RequestId,
                    Narad.Contracts.NaradEventBodies.ApprovalWaitId(approval.RequestId),
                    "approval",
                    ["approve", "deny"],
                    approval.ToolCallId,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["toolCallId"] = approval.ToolCallId,
                        ["toolName"] = approval.ToolName,
                        ["args"] = string.IsNullOrWhiteSpace(approval.ArgumentsJson)
                            ? new JsonObject()
                            : JsonNode.Parse(approval.ArgumentsJson),
                    }, KautukaJsonContext.Default.JsonObject));
                sessionId = approval.SessionId ?? sessionId;
                agentId = approval.AgentId ?? agentId;
                runId = approval.RunId ?? runId;
                break;
            case GatewayChatRunPaused paused:
                eventType = NaradEventTypes.RunPaused;
                body = NaradEventBodies.RunPaused(paused.WaitId, "interrupt", paused.InterruptId);
                break;
            case GatewayChatContinuationResumed resumed:
                eventType = NaradEventTypes.RunResumed;
                body = NaradEventBodies.RunResumed(resumed.WaitId);
                break;
            case GatewayChatProduceBoundarySuspended:
                throw new InvalidOperationException(
                    "GatewayChatProduceBoundarySuspended is not an Narad authority event; discard it in the producer.");
            case GatewayChatStreamDone done:
                if (AgentExecutionTerminalEvent.IsWaitingOnChildBoundary(done))
                {
                    throw new InvalidOperationException(
                        "AAAT waiting_on_child StreamDone is not an Narad authority event.");
                }

                if (string.Equals(done.Result.Run?.Phase, GatewayRunPhases.AwaitingToolApproval, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "AwaitingToolApproval StreamDone is not an Narad authority event; pause comes from GatewayChatApprovalRequest.");
                }

                eventType = done.Result.Run?.Phase switch
                {
                    GatewayRunPhases.Stopped => NaradEventTypes.RunCancelled,
                    GatewayRunPhases.Failed or GatewayRunPhases.Aborted => NaradEventTypes.RunError,
                    _ => NaradEventTypes.RunFinished,
                };
                body = JsonSerializer.SerializeToElement(done, KautukaJsonContext.Default.GatewayChatStreamDone);
                sessionId = done.Result.SessionId;
                agentId = done.Result.TargetId;
                break;
            default:
                throw new InvalidOperationException($"Unsupported test chat event '{chatEvent.GetType().Name}'.");
        }

        return FromBody(eventType, body, sessionId, runId, runId, sessionSequence, rootSequence);
    }

    public static async IAsyncEnumerable<NaradPersistedEvent> ToNaradEvents(
        IAsyncEnumerable<GatewayChatStreamEvent> chatEvents,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long sessionSequence = 0;
        long rootSequence = 0;
        await foreach (GatewayChatStreamEvent chatEvent in chatEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chatEvent is GatewayChatProduceBoundarySuspended)
            {
                continue;
            }

            // Mirror AgentExecutionEventWriter: approval → interrupt + pause; StreamDone approval silent.
            if (chatEvent is GatewayChatStreamDone done
                && (AgentExecutionTerminalEvent.IsWaitingOnChildBoundary(done)
                    || string.Equals(done.Result.Run?.Phase, GatewayRunPhases.AwaitingToolApproval, StringComparison.Ordinal)))
            {
                continue;
            }

            if (chatEvent is GatewayChatApprovalRequest approval)
            {
                yield return FromChatEvent(
                    approval,
                    sessionSequence: ++sessionSequence,
                    rootSequence: ++rootSequence);
                if (approval.PauseRun)
                {
                    string waitId = NaradEventBodies.ApprovalWaitId(approval.RequestId);
                    yield return FromBody(
                        NaradEventTypes.RunPaused,
                        NaradEventBodies.RunPaused(waitId, "interrupt", approval.RequestId),
                        sessionId: approval.SessionId ?? "session-test",
                        runId: approval.RunId ?? "run-test",
                        rootRunId: approval.RunId ?? "run-test",
                        sessionSequence: ++sessionSequence,
                        rootSequence: ++rootSequence);
                }

                continue;
            }

            if (chatEvent is GatewayChatRunPaused paused)
            {
                yield return FromBody(
                    NaradEventTypes.RunPaused,
                    NaradEventBodies.RunPaused(paused.WaitId, "interrupt", paused.InterruptId),
                    sessionId: "session-test",
                    runId: "run-test",
                    rootRunId: "run-test",
                    sessionSequence: ++sessionSequence,
                    rootSequence: ++rootSequence);
                continue;
            }

            if (chatEvent is GatewayChatStreamDone doneWithResponse && !string.IsNullOrWhiteSpace(doneWithResponse.Result.Response))
            {
                yield return FromChatEvent(
                    new GatewayChatTokenDelta(doneWithResponse.Result.Response, IsThinking: false),
                    sessionSequence: ++sessionSequence,
                    rootSequence: ++rootSequence);
            }

            yield return FromChatEvent(
                chatEvent,
                sessionSequence: ++sessionSequence,
                rootSequence: ++rootSequence);
        }
    }

    public static NaradPersistedEvent FromDraft(
        NaradAuthorityEventDraft draft,
        NaradEventContext _,
        long sessionSequence,
        long? rootSequence)
    {
        var envelope = new JsonObject
        {
            ["type"] = draft.EventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = draft.EventId,
            ["timestamp"] = draft.OccurredAt,
            ["sessionId"] = draft.SessionId,
            ["sessionSequence"] = sessionSequence,
        };

        if (!string.IsNullOrWhiteSpace(draft.RootRunId) && !string.IsNullOrWhiteSpace(draft.RunId) && rootSequence.HasValue)
        {
            envelope["rootRunId"] = draft.RootRunId;
            envelope["runId"] = draft.RunId;
            envelope["sequence"] = rootSequence.Value;
        }

        if (draft.Body.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in draft.Body.EnumerateObject())
            {
                envelope[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return new NaradPersistedEvent(
            draft.EventId,
            NaradProtocolVersions.V1,
            draft.EventType,
            draft.SessionId,
            sessionSequence,
            draft.RecordClass,
            envelope.ToJsonString(),
            draft.OccurredAt,
            draft.RootRunId,
            draft.RunId,
            rootSequence,
            draft.WorkflowId,
            null);
    }

    internal static NaradPersistedEvent FromBody(
        string eventType,
        JsonElement body,
        string sessionId,
        string runId,
        string rootRunId,
        long sessionSequence,
        long rootSequence)
    {
        string eventId = Guid.NewGuid().ToString("N");
        string timestamp = DateTimeOffset.UtcNow.ToString("O");
        var envelope = new JsonObject
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = timestamp,
            ["sessionId"] = sessionId,
            ["sessionSequence"] = sessionSequence,
            ["rootRunId"] = rootRunId,
            ["runId"] = runId,
            ["sequence"] = rootSequence,
        };

        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in body.EnumerateObject())
            {
                envelope[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            sessionId,
            sessionSequence,
            GatewayRuntimeRecordClasses.Message,
            envelope.ToJsonString(),
            timestamp,
            rootRunId,
            runId,
            rootSequence,
            null,
            null);
    }
}
