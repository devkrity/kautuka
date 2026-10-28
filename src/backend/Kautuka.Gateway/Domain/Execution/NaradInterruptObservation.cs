using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record NaradInterruptRequestedFields(
    string InterruptId,
    string WaitId,
    string Kind,
    string? ToolCallId,
    string? ToolName,
    string? InputJson);

internal static class NaradInterruptObservation
{
    public static NaradInterruptRequestedFields Parse(NaradPersistedEvent persistedEvent)
    {
        if (!string.Equals(persistedEvent.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected '{NaradEventTypes.InterruptRequested}' but received '{persistedEvent.EventType}'.");
        }

        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        string? interruptId = NaradPersistedEventReader.String(body, "interruptId")
            ?? NaradPersistedEventReader.String(body, "requestId");
        if (string.IsNullOrWhiteSpace(interruptId))
        {
            throw new InvalidOperationException("interrupt.requested is missing interruptId.");
        }
        string waitId = NaradPersistedEventReader.String(body, "waitId")
            ?? throw new InvalidOperationException("interrupt.requested is missing waitId.");
        string kind = NaradPersistedEventReader.String(body, "kind") ?? "approval";
        string? toolCallId = NaradPersistedEventReader.String(body, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId)
            && body.TryGetProperty("input", out JsonElement inputElement)
            && inputElement.ValueKind == JsonValueKind.Object)
        {
            toolCallId = NaradPersistedEventReader.String(inputElement, "toolCallId");
        }

        string? inputJson = body.TryGetProperty("input", out JsonElement input)
            ? input.GetRawText()
            : null;
        string? toolName = NaradPersistedEventReader.String(body, "toolName");
        return new NaradInterruptRequestedFields(interruptId, waitId, kind, toolCallId, toolName, inputJson);
    }

    public static GatewayChatApprovalRequest? TryBuildChatApprovalRequest(
        NaradInterruptRequestedFields fields,
        IGatewayApprovalStore approvalStore)
    {
        GatewayToolApprovalRequest? stored = approvalStore.GetById(fields.InterruptId);
        if (stored is null)
        {
            return null;
        }

        return new GatewayChatApprovalRequest(
            stored.Id,
            stored.ToolCallId,
            stored.ToolName,
            stored.ArgumentsJson ?? fields.InputJson,
            stored.RunId,
            stored.CheckpointId,
            stored.McpServerId,
            stored.ToolType,
            OuterSessionId: stored.OuterSessionId,
            OuterAgentId: stored.OuterAgentId,
            ExpiresAt: stored.ExpiresAt,
            AgentId: stored.AgentId,
            SessionId: stored.SessionId);
    }

    public static GatewayChatApprovalRequest BuildFallbackChatApprovalRequest(NaradInterruptRequestedFields fields)
        => new(
            fields.InterruptId,
            fields.ToolCallId ?? string.Empty,
            ToolName: fields.ToolName ?? string.Empty,
            ArgumentsJson: fields.InputJson);

    public static GatewayChatApprovalRequest ResolveChatApprovalRequest(
        NaradPersistedEvent persistedEvent,
        IGatewayApprovalStore approvalStore,
        string? fallbackAgentId = null,
        string? fallbackSessionId = null)
    {
        NaradInterruptRequestedFields fields = Parse(persistedEvent);
        GatewayChatApprovalRequest? resolved = TryBuildChatApprovalRequest(fields, approvalStore);
        if (resolved is not null)
        {
            return resolved with
            {
                RunId = resolved.RunId ?? persistedEvent.RunId,
                AgentId = resolved.AgentId ?? fallbackAgentId,
                SessionId = resolved.SessionId ?? fallbackSessionId ?? persistedEvent.SessionId,
            };
        }

        GatewayChatApprovalRequest fallback = BuildFallbackChatApprovalRequest(fields);
        return fallback with
        {
            RunId = fallback.RunId ?? persistedEvent.RunId,
            AgentId = fallbackAgentId,
            SessionId = fallbackSessionId ?? persistedEvent.SessionId,
        };
    }
}
