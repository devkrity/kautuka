using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

/// <summary>
/// Redacts Narad authority bodies to the channel wire vocabulary before outbox enqueue.
/// Mirrors ChannelHost translator field selection; never forwards operator-only or protected data.
/// Accumulates assistant text in Gateway so completed / finished payloads carry content
/// without relying on ChannelHost process-local delta state.
/// </summary>
internal static class ChannelNaradProjectionPayload
{
    internal static class WireEventTypes
    {
        public const string UserMessageRecorded = "user.message.recorded";
        public const string AssistantTextDelta = "assistant.text.delta";
        public const string AssistantMessageCompleted = "assistant.message.completed";
        public const string RunFinished = "run.finished";
        public const string ToolApprovalRequested = "tool.approval.requested";
        public const string ToolApprovalResolved = "tool.approval.resolved";
    }

    private static readonly ConcurrentDictionary<string, StringBuilder> textByMessage = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> lastTextByRun = new(StringComparer.Ordinal);

    private static readonly HashSet<string> operatorOnlyEventTypes = new(StringComparer.Ordinal)
    {
        NaradEventTypes.TraceEvent,
        NaradEventTypes.ProgressRecorded,
        NaradEventTypes.SessionRevision,
        NaradEventTypes.ToolCallStarted,
        NaradEventTypes.ToolCallFinished,
        NaradEventTypes.ReasoningStarted,
        NaradEventTypes.ReasoningDelta,
        NaradEventTypes.ReasoningCompleted,
    };

    public static bool TryBuild(
        NaradPersistedEvent persistedEvent,
        out string wireEventType,
        out string payloadJson)
    {
        wireEventType = string.Empty;
        payloadJson = string.Empty;

        if (operatorOnlyEventTypes.Contains(persistedEvent.EventType))
        {
            return false;
        }

        string validated;
        try
        {
            validated = NaradStreamObservation.ValidateEventJson(persistedEvent);
        }
        catch (JsonException)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(validated);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonObject? sanitized = persistedEvent.EventType switch
        {
            NaradEventTypes.MessageUserRecorded => SanitizeUserMessage(root),
            NaradEventTypes.MessageTextDelta => SanitizeTextDelta(root),
            NaradEventTypes.MessageTextCompleted => SanitizeMessageCompleted(root),
            NaradEventTypes.RunFinished => SanitizeRunFinished(root),
            NaradEventTypes.InterruptRequested => SanitizeApprovalRequested(root),
            NaradEventTypes.InterruptResolved => SanitizeApprovalResolved(root),
            _ => null,
        };

        if (sanitized is null)
        {
            return false;
        }

        wireEventType = MapWireEventType(persistedEvent.EventType);
        payloadJson = sanitized.ToJsonString();
        return true;
    }

    public static void AccumulateTextDelta(string registrationId, string rootRunId, string payloadJson)
    {
        if (!TryReadObject(payloadJson, out JsonObject payload))
        {
            return;
        }

        string? messageId = payload["messageId"]?.GetValue<string>();
        string? delta = payload["delta"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrEmpty(delta))
        {
            return;
        }

        string key = MessageKey(registrationId, rootRunId, messageId);
        StringBuilder builder = textByMessage.GetOrAdd(key, static _ => new StringBuilder());
        lock (builder)
        {
            builder.Append(delta);
            lastTextByRun[RunKey(registrationId, rootRunId)] = builder.ToString();
        }
    }

    public static string AttachAccumulatedContent(string registrationId, string rootRunId, string payloadJson)
    {
        if (!TryReadObject(payloadJson, out JsonObject payload))
        {
            return payloadJson;
        }

        string? messageId = payload["messageId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return payloadJson;
        }

        string key = MessageKey(registrationId, rootRunId, messageId);
        string? content = null;
        if (textByMessage.TryRemove(key, out StringBuilder? builder))
        {
            lock (builder)
            {
                content = builder.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(content)
            && lastTextByRun.TryGetValue(RunKey(registrationId, rootRunId), out string? last))
        {
            content = last;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return payloadJson;
        }

        payload["content"] = content;
        lastTextByRun[RunKey(registrationId, rootRunId)] = content;
        return payload.ToJsonString();
    }

    public static string AttachRunFinishedFallbackText(string registrationId, string rootRunId, string payloadJson)
    {
        if (HasRunFinishedResponse(payloadJson))
        {
            lastTextByRun.TryRemove(RunKey(registrationId, rootRunId), out _);
            return payloadJson;
        }

        if (!lastTextByRun.TryRemove(RunKey(registrationId, rootRunId), out string? content)
            || string.IsNullOrWhiteSpace(content))
        {
            return payloadJson;
        }

        if (!TryReadObject(payloadJson, out JsonObject payload))
        {
            payload = new JsonObject();
        }

        payload["response"] = content;
        return payload.ToJsonString();
    }

    public static bool HasContent(string payloadJson)
    {
        if (!TryReadObject(payloadJson, out JsonObject payload))
        {
            return false;
        }

        string? content = payload["content"]?.GetValue<string>()
            ?? payload["text"]?.GetValue<string>()
            ?? payload["message"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(content);
    }

    public static bool HasRunFinishedResponse(string payloadJson)
    {
        if (!TryReadObject(payloadJson, out JsonObject payload))
        {
            return false;
        }

        string? response = payload["response"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(response))
        {
            return true;
        }

        if (payload["result"] is JsonObject result
            && result["response"] is JsonValue nested
            && nested.GetValueKind() == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(nested.GetValue<string>());
        }

        return false;
    }

    private static string MessageKey(string registrationId, string rootRunId, string messageId)
        => $"{registrationId}:{rootRunId}:{messageId}";

    private static string RunKey(string registrationId, string rootRunId)
        => $"{registrationId}:{rootRunId}";

    private static bool TryReadObject(string payloadJson, out JsonObject payload)
    {
        payload = null!;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject obj)
            {
                return false;
            }

            payload = obj;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string MapWireEventType(string naradEventType)
        => naradEventType switch
        {
            NaradEventTypes.MessageUserRecorded => WireEventTypes.UserMessageRecorded,
            NaradEventTypes.MessageTextDelta => WireEventTypes.AssistantTextDelta,
            NaradEventTypes.MessageTextCompleted => WireEventTypes.AssistantMessageCompleted,
            NaradEventTypes.RunFinished => WireEventTypes.RunFinished,
            NaradEventTypes.InterruptRequested => WireEventTypes.ToolApprovalRequested,
            NaradEventTypes.InterruptResolved => WireEventTypes.ToolApprovalResolved,
            _ => naradEventType,
        };

    private static JsonObject? SanitizeUserMessage(JsonElement root)
    {
        string? messageId = ReadString(root, "messageId");
        string? content = ReadString(root, "content") ?? ReadString(root, "message") ?? string.Empty;
        JsonArray? attachmentIds = ReadStringArray(root, "attachmentIds");
        if (string.IsNullOrWhiteSpace(messageId)
            || (string.IsNullOrWhiteSpace(content) && (attachmentIds is null || attachmentIds.Count == 0)))
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["messageId"] = messageId,
            ["content"] = content,
        };
        if (attachmentIds is { Count: > 0 })
        {
            payload["attachmentIds"] = attachmentIds;
        }

        return payload;
    }

    private static JsonObject? SanitizeTextDelta(JsonElement root)
    {
        if (ReadBool(root, "isThinking"))
        {
            return null;
        }

        string? messageId = ReadString(root, "messageId");
        string? delta = ReadString(root, "delta");
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(delta))
        {
            return null;
        }

        return new JsonObject
        {
            ["messageId"] = messageId,
            ["delta"] = delta,
        };
    }

    private static JsonObject? SanitizeMessageCompleted(JsonElement root)
    {
        string? messageId = ReadString(root, "messageId");
        return string.IsNullOrWhiteSpace(messageId)
            ? null
            : new JsonObject { ["messageId"] = messageId };
    }

    private static JsonObject? SanitizeRunFinished(JsonElement root)
    {
        string? response = ReadString(root, "response");
        if (string.IsNullOrWhiteSpace(response)
            && root.TryGetProperty("result", out JsonElement result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            response = nested.GetString();
        }

        return string.IsNullOrWhiteSpace(response)
            ? new JsonObject()
            : new JsonObject { ["response"] = response };
    }

    private static JsonObject? SanitizeApprovalRequested(JsonElement root)
    {
        string requestId = ReadString(root, "interruptId")
            ?? ReadString(root, "requestId")
            ?? ReadString(root, "approvalRequestId")
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        string toolName = ReadString(root, "toolName")
            ?? ReadNestedString(root, "input", "toolName")
            ?? "tool";
        var payload = new JsonObject
        {
            ["requestId"] = requestId,
            ["toolName"] = toolName,
        };

        string? prompt = ReadString(root, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            payload["prompt"] = prompt;
        }

        string? userMessage = ReadString(root, "userMessage") ?? ReadString(root, "message");
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            payload["userMessage"] = userMessage;
        }

        string? argumentsJson = ReadString(root, "argumentsJson");
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            payload["argumentsJson"] = argumentsJson;
        }

        CopyWorkflowReviewDisplayFields(root, payload);

        return payload;
    }

    private static JsonObject? SanitizeApprovalResolved(JsonElement root)
    {
        string? decision = ReadString(root, "decision");
        if (string.IsNullOrWhiteSpace(decision))
        {
            return null;
        }

        var payload = new JsonObject { ["decision"] = decision };
        string? toolName = ReadString(root, "toolName");
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            payload["toolName"] = toolName;
        }

        string? transportMessageId = ReadString(root, "transportMessageId")
            ?? ReadString(root, "sourceTransportMessageId");
        if (!string.IsNullOrWhiteSpace(transportMessageId))
        {
            payload["transportMessageId"] = transportMessageId;
        }

        string? requestPrompt = ReadString(root, "prompt");
        if (!string.IsNullOrWhiteSpace(requestPrompt)) payload["prompt"] = requestPrompt;

        string? argumentsJson = ReadString(root, "argumentsJson");
        if (!string.IsNullOrWhiteSpace(argumentsJson)) payload["argumentsJson"] = argumentsJson;

        string? reason = ReadString(root, "reason");
        if (!string.IsNullOrWhiteSpace(reason))
        {
            payload["reason"] = reason;
        }

        CopyWorkflowReviewDisplayFields(root, payload);

        return payload;
    }

    private static void CopyWorkflowReviewDisplayFields(JsonElement root, JsonObject payload)
    {
        string? kind = ReadString(root, "kind");
        if (!string.Equals(kind, "workflow_review", StringComparison.Ordinal)) return;
        payload["kind"] = kind;

        string? title = ReadString(root, "title");
        if (!string.IsNullOrWhiteSpace(title)) payload["title"] = title[..Math.Min(title.Length, 200)];
        string? summary = ReadString(root, "summary");
        if (!string.IsNullOrWhiteSpace(summary)) payload["summary"] = summary[..Math.Min(summary.Length, 500)];

        if (root.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
        {
            JsonArray safeFields = [];
            foreach (JsonElement field in fields.EnumerateArray().Take(10))
            {
                string? label = ReadString(field, "label");
                string? value = ReadString(field, "value");
                if (string.IsNullOrWhiteSpace(label) || value is null) continue;
                safeFields.Add((JsonNode?)new JsonObject
                {
                    ["label"] = label[..Math.Min(label.Length, 80)],
                    ["value"] = value[..Math.Min(value.Length, 300)],
                });
            }
            payload["fields"] = safeFields;
        }

        if (root.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
        {
            JsonArray safeActions = [];
            foreach (JsonElement action in actions.EnumerateArray().Take(5))
            {
                string? value = ReadString(action, "value");
                string? label = ReadString(action, "label");
                string? style = ReadString(action, "style");
                if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(label)) continue;
                safeActions.Add((JsonNode?)new JsonObject
                {
                    ["value"] = value[..Math.Min(value.Length, 48)],
                    ["label"] = label[..Math.Min(label.Length, 80)],
                    ["style"] = style is "primary" or "danger" or "default" ? style : "default",
                });
            }
            payload["actions"] = safeActions;
        }
    }

    private static JsonArray? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new JsonArray();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is string text && text.Length > 0)
            {
                items.Add((JsonNode?)text);
            }
        }

        return items.Count == 0 ? null : items;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement root, string parentName, string name)
    {
        if (!root.TryGetProperty(parentName, out JsonElement parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(parent, name);
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;
}
