using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class ChatGatewayService
{
    private async ValueTask<NaradPersistedEvent> PublishCompactAsync(
        string eventType,
        JsonElement body,
        NaradEventContext context,
        CancellationToken cancellationToken)
    {
        NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(eventType, body, context);
        return await runtimeEventPublisher.PublishAsync(draft, context, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminalOrPaused(string eventType)
        => eventType is NaradEventTypes.RunFinished
            or NaradEventTypes.RunError
            or NaradEventTypes.RunCancelled
            or NaradEventTypes.RunPaused;

    private static GatewayChatStreamDone ResolveStreamTerminalDone(
        NaradPersistedEvent persistedEvent,
        string targetType,
        string targetId,
        string sessionId)
    {
        // Narad run.finished / run.error / run.paused bodies are authority vocabularies, not a
        // legacy GatewayChatStreamDone envelope. Deserializing run.finished as StreamDone (which
        // expected Result.TargetType/TargetId) left those fields empty after Narad cutover and
        // crashed AppendRun with "Runtime sessions must target an agent."
        // Always rebuild chat-result shape from the request context + stamped response text.
        return NaradStreamObservation.BuildTerminalDone(persistedEvent, targetType, targetId, sessionId);
    }

    private static void TryRecordPausedToolCallStarted(
        NaradPersistedEvent persistedEvent,
        string targetId,
        List<GatewayToolCall> pausedOpenToolCalls)
    {
        if (!string.Equals(persistedEvent.EventType, NaradEventTypes.ToolCallStarted, StringComparison.Ordinal))
        {
            return;
        }

        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        string? toolCallId = NaradPersistedEventReader.String(body, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        string toolName = NaradPersistedEventReader.String(body, "toolName") ?? string.Empty;
        string argumentsJson = body.TryGetProperty("args", out JsonElement argsElement)
            ? argsElement.GetRawText()
            : "{}";
        string actionKind = NaradPersistedEventReader.String(body, "executor") ?? "mcp";
        if (pausedOpenToolCalls.Any(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal)))
        {
            return;
        }

        pausedOpenToolCalls.Add(new GatewayToolCall(
            toolCallId,
            toolName,
            targetId,
            argumentsJson,
            null,
            "calling",
            actionKind));
    }

    private static bool ReadBooleanProperty(JsonElement body, string propertyName)
        => body.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;

    private ToolApprovalResponseInfo ResolveApprovalContinuationResponse(
        ToolApprovalResponseInfo approvalResponse,
        out RunWaitState? waitState)
    {
        waitState = PackedToolApproval.FindActiveWait(
            runWaitStateStore,
            approvalResponse.RequestId,
            approvalResponse.ContinuationRunId);
        if (waitState is null)
        {
            return approvalResponse;
        }

        ToolApprovalResponseInfo withContinuation = string.IsNullOrWhiteSpace(approvalResponse.ContinuationRunId)
            ? approvalResponse with { ContinuationRunId = waitState.ContinuationRunId }
            : approvalResponse;
        return PackedToolApproval.AttachPackResponses(approvalStore, waitState, withContinuation);
    }
}
