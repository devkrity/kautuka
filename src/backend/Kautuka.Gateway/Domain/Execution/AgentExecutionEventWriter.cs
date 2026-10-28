using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class AgentExecutionEventWriter(IRuntimeEventPublisher runtimeEventPublisher, IRuntimeEventHistoryReader? historyReader = null)
{
    /// <summary>
    /// Maps early lifecycle domain events to Narad without an external channel consumer.
    /// </summary>
    public async ValueTask<IReadOnlyList<NaradPersistedEvent>> MapAsync(
        GatewayChatStreamEvent chatEvent,
        NaradEventContext context,
        AssistantMessageLifecycle messageLifecycle,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        await WriteAsync(channel.Writer, chatEvent, context, messageLifecycle, cancellationToken)
            .ConfigureAwait(false);
        channel.Writer.TryComplete();
        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persisted))
        {
            events.Add(persisted);
        }

        return events;
    }

    public async ValueTask<NaradPersistedEvent?> WriteAsync(
        ChannelWriter<NaradPersistedEvent> writer,
        GatewayChatStreamEvent chatEvent,
        NaradEventContext context,
        AssistantMessageLifecycle messageLifecycle,
        CancellationToken cancellationToken)
    {
        NaradPersistedEvent? lastEvent = null;
        async ValueTask PublishAsync(string eventType, JsonElement payload)
            => lastEvent = await WriteRuntimeAsync(
                writer,
                eventType,
                payload,
                context,
                cancellationToken).ConfigureAwait(false);

        async ValueTask CloseAssistantLifecycleAsync()
        {
            if (messageLifecycle.ReasoningOpen)
            {
                await PublishAsync(
                    NaradEventTypes.ReasoningCompleted,
                    NaradEventBodies.ReasoningCompleted(
                        messageLifecycle.ReasoningMessageId,
                        messageLifecycle.MessageId)).ConfigureAwait(false);
                messageLifecycle.ReasoningOpen = false;
            }

            if (messageLifecycle.MessageOpen)
            {
                await PublishAsync(
                    NaradEventTypes.MessageTextCompleted,
                    NaradEventBodies.MessageTextCompleted(messageLifecycle.MessageId)).ConfigureAwait(false);
                messageLifecycle.MessageOpen = false;
            }
        }

        switch (chatEvent)
        {
            case GatewayChatTokenDelta delta:
                if (delta.IsThinking && !messageLifecycle.ReasoningOpen)
                {
                    await PublishAsync(
                        NaradEventTypes.ReasoningStarted,
                        NaradEventBodies.ReasoningStarted(
                            messageLifecycle.ReasoningMessageId,
                            messageLifecycle.MessageId)).ConfigureAwait(false);
                    messageLifecycle.ReasoningOpen = true;
                }
                else if (!delta.IsThinking && messageLifecycle.ReasoningOpen)
                {
                    await PublishAsync(
                        NaradEventTypes.ReasoningCompleted,
                        NaradEventBodies.ReasoningCompleted(
                            messageLifecycle.ReasoningMessageId,
                            messageLifecycle.MessageId)).ConfigureAwait(false);
                    messageLifecycle.ReasoningOpen = false;
                }

                if (!delta.IsThinking && !messageLifecycle.MessageOpen)
                {
                    await PublishAsync(
                        NaradEventTypes.MessageTextStarted,
                        NaradEventBodies.MessageTextStarted(messageLifecycle.MessageId)).ConfigureAwait(false);
                    messageLifecycle.MessageOpen = true;
                }

                if (delta.IsThinking)
                {
                    await PublishAsync(
                        NaradEventTypes.ReasoningDelta,
                        NaradEventBodies.ReasoningDelta(
                            messageLifecycle.ReasoningMessageId,
                            delta.Delta,
                            messageLifecycle.MessageId)).ConfigureAwait(false);
                }
                else
                {
                    await PublishAsync(
                        NaradEventTypes.MessageTextDelta,
                        NaradEventBodies.MessageTextDelta(messageLifecycle.MessageId, delta.Delta)).ConfigureAwait(false);
                }

                break;
            case GatewayChatToolCall toolCall:
                if (AgentToolCallReplayGuard.IsAlreadyStarted(historyReader, context, toolCall)) break;
                await PublishAsync(
                    NaradEventTypes.ToolCallStarted,
                    AgentExecutionNaradToolBodies.ToolCallStarted(toolCall)).ConfigureAwait(false);
                break;
            case GatewayChatViewOpened opened:
                {
                    JsonNode? parsed = JsonNode.Parse(opened.ViewJson);
                    if (parsed is JsonObject model)
                    {
                        string viewId = model["viewId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N")[..12];
                        model["toolCallId"] = opened.ToolCallId;
                        var viewBody = new JsonObject
                        {
                            ["viewId"] = viewId,
                            ["model"] = model,
                        };
                        await PublishAsync(
                            NaradEventTypes.ViewOpened,
                            JsonSerializer.SerializeToElement(viewBody, KautukaJsonContext.Default.JsonObject)).ConfigureAwait(false);
                    }
                    break;
                }
            case GatewayChatToolResult toolResult:
                await PublishAsync(
                    NaradEventTypes.ToolCallFinished,
                    AgentExecutionNaradToolBodies.ToolCallFinished(
                        toolResult.ToolCallId,
                        outcome: "success",
                        resultJson: toolResult.ResultJson)).ConfigureAwait(false);
                break;
            case GatewayChatToolFailure toolFailure:
                string toolFailureOutcome = toolFailure.Code is "cancelled" or "stopped" or "user_stopped" or "denied"
                    ? "cancelled"
                    : "error";
                await PublishAsync(
                    NaradEventTypes.ToolCallFinished,
                    AgentExecutionNaradToolBodies.ToolCallFinished(
                        toolFailure.ToolCallId,
                        outcome: toolFailureOutcome,
                        toolName: toolFailure.ToolName,
                        code: toolFailure.Code,
                        message: toolFailure.Message)).ConfigureAwait(false);
                break;
            case GatewayChatRunStarted runStarted:
                {
                    var body = new JsonObject
                    {
                        ["trigger"] = runStarted.Trigger,
                    };
                    if (!string.IsNullOrWhiteSpace(runStarted.StageTransitionId))
                    {
                        body["stageTransitionId"] = runStarted.StageTransitionId;
                    }

                    if (!string.IsNullOrWhiteSpace(runStarted.ParentRunId))
                    {
                        body["parentRunId"] = runStarted.ParentRunId;
                    }

                    if (!string.IsNullOrWhiteSpace(runStarted.ParentToolCallId))
                    {
                        body["parentToolCallId"] = runStarted.ParentToolCallId;
                    }

                    await PublishAsync(
                        NaradEventTypes.RunStarted,
                        JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject)).ConfigureAwait(false);
                    break;
                }
            case GatewayChatUserMessageRecorded userMessage:
                await PublishAsync(
                    NaradEventTypes.MessageUserRecorded,
                    NaradEventBodies.MessageUserRecorded(
                        userMessage.MessageId,
                        userMessage.Content,
                        attachmentIds: userMessage.AttachmentIds)).ConfigureAwait(false);
                break;
            case GatewayChatMessageTextStarted:
                if (!messageLifecycle.MessageOpen)
                {
                    await PublishAsync(
                        NaradEventTypes.MessageTextStarted,
                        NaradEventBodies.MessageTextStarted(messageLifecycle.MessageId)).ConfigureAwait(false);
                    messageLifecycle.MessageOpen = true;
                }

                break;
            case GatewayChatApprovalRequest approval:
                await CloseAssistantLifecycleAsync().ConfigureAwait(false);
                string waitId = NaradEventBodies.ApprovalWaitId(approval.RequestId);
                string[] interruptActions = ApprovalActionCatalog
                    .ForToolType(approval.ToolType ?? "mcp")
                    .Select(static action => action.Value)
                    .ToArray();
                lastEvent = await WriteRuntimeAsync(
                    writer,
                    NaradEventTypes.InterruptRequested,
                    NaradEventBodies.InterruptRequested(
                        approval.RequestId,
                        waitId,
                        "approval",
                        interruptActions,
                        approval.ToolCallId,
                        PackedToolApproval.ToInterruptRequestedInput(approval)),
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (approval.PauseRun)
                {
                    await WriteRuntimeAsync(
                        writer,
                        NaradEventTypes.RunPaused,
                        NaradEventBodies.RunPaused(waitId, "interrupt", approval.RequestId),
                        context,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            case GatewayChatRunPaused paused:
                if (string.IsNullOrWhiteSpace(paused.WaitId))
                {
                    throw new InvalidOperationException(
                        "GatewayChatRunPaused requires a non-empty waitId for run.paused.");
                }

                await CloseAssistantLifecycleAsync().ConfigureAwait(false);
                lastEvent = await WriteRuntimeAsync(
                    writer,
                    NaradEventTypes.RunPaused,
                    NaradEventBodies.RunPaused(paused.WaitId, "interrupt", paused.InterruptId),
                    context,
                    cancellationToken).ConfigureAwait(false);
                break;
            case GatewayChatContinuationCheckpoint:
                return null;
            case GatewayChatContinuationResumed resumed:
                // Domain resume fact only — never invent from wait/BoundaryType alone.
                // Empty waitId is not a legal Narad resume.
                if (string.IsNullOrWhiteSpace(resumed.WaitId))
                {
                    throw new InvalidOperationException(
                        "GatewayChatContinuationResumed requires a non-empty waitId for run.resumed.");
                }

                await PublishAsync(
                    NaradEventTypes.RunResumed,
                    NaradEventBodies.RunResumed(resumed.WaitId)).ConfigureAwait(false);
                break;
            case GatewayChatProduceBoundarySuspended:
                // Non-terminal produce boundary (AAAT waiting_on_child): close any open
                // assistant message/reasoning fragments, then publish nothing further.
                await CloseAssistantLifecycleAsync().ConfigureAwait(false);
                break;
            case GatewayChatStreamDone done:
                await CloseAssistantLifecycleAsync().ConfigureAwait(false);
                // Tool-approval StreamDone follows interrupt.requested + run.paused already
                // published on GatewayChatApprovalRequest / GatewayChatRunPaused; do not emit a second lifecycle.
                if (string.Equals(done.Result.Run?.Phase, GatewayRunPhases.AwaitingToolApproval, StringComparison.Ordinal))
                {
                    break;
                }

                // waiting_on_child must use GatewayChatProduceBoundarySuspended, never StreamDone.
                if (AgentExecutionTerminalEvent.IsWaitingOnChildBoundary(done))
                {
                    throw new InvalidOperationException(
                        "AAAT waiting_on_child must complete via GatewayChatProduceBoundarySuspended, not StreamDone.");
                }

                AgentExecutionTerminalEvent terminal = AgentExecutionTerminalEvent.From(done);
                await PublishAsync(terminal.EventType, terminal.Payload).ConfigureAwait(false);
                break;
            case GatewayChatSessionReady:
            case GatewayChatRunPhase:
            case GatewayChatAgentTransition:
                return null;
            case GatewayChatWorkflowEvent workflow:
                await PublishAsync(
                    NaradEventTypes.ProgressRecorded,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["category"] = "runtime",
                        ["label"] = workflow.EventType,
                        ["executorId"] = workflow.ExecutorId,
                    }, KautukaJsonContext.Default.JsonObject)).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported execution event '{chatEvent.GetType().Name}'.");
        }

        return lastEvent;
    }

    public async ValueTask<NaradPersistedEvent> WriteRuntimeAsync(
        ChannelWriter<NaradPersistedEvent> writer,
        string eventType,
        JsonElement body,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
    {
        NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(eventType, body, context);
        NaradPersistedEvent persistedEvent = await runtimeEventPublisher.PublishAsync(draft, context, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(persistedEvent, cancellationToken).ConfigureAwait(false);
        return persistedEvent;
    }
}
