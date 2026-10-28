using System.Text.Json;
using System.Text.Json.Nodes;

namespace Narad.Contracts;

/// <summary>Minimal Narad event body factories using spec vocabulary.</summary>
public static class NaradEventBodies
{
    public static JsonElement RunStarted(
        string? trigger = null,
        string? clientExecutorId = null,
        string? parentRunId = null,
        string? parentToolCallId = null)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            body["trigger"] = trigger;
        }

        if (!string.IsNullOrWhiteSpace(clientExecutorId))
        {
            body["clientExecutorId"] = clientExecutorId;
        }

        if (!string.IsNullOrWhiteSpace(parentRunId))
        {
            body["parentRunId"] = parentRunId;
        }

        if (!string.IsNullOrWhiteSpace(parentToolCallId))
        {
            body["parentToolCallId"] = parentToolCallId;
        }

        return Serialize(body);
    }

    public static string BuildUserMessageId(string rootRunId, string runId)
        => $"user:{rootRunId}:{runId}";

    public static string BuildAssistantMessageId(string rootRunId, string runId, string? deliveryAttemptId = null)
        => string.IsNullOrWhiteSpace(deliveryAttemptId)
            ? $"assistant:{rootRunId}:{runId}"
            : $"assistant:{rootRunId}:{runId}:{deliveryAttemptId}";

    public static string BuildReasoningMessageId(string messageId)
        => $"reasoning:{messageId}";

    public static JsonElement MessageUserRecorded(
        string messageId,
        string content,
        string messageScope = "run",
        IReadOnlyList<string>? attachmentIds = null,
        string? sourceChannel = null)
    {
        var body = new JsonObject
        {
            ["messageId"] = messageId,
            ["messageScope"] = messageScope,
            ["content"] = content,
        };
        if (attachmentIds is { Count: > 0 })
        {
            body["attachmentIds"] = new JsonArray([.. attachmentIds.Select(static id => (JsonNode?)id)]);
        }

        if (!string.IsNullOrWhiteSpace(sourceChannel))
        {
            body["sourceChannel"] = sourceChannel.Trim();
        }

        return Serialize(body);
    }

    public static JsonElement MessageTextStarted(string messageId, string role = "assistant", string messageScope = "run")
        => Serialize(new JsonObject
        {
            ["messageId"] = messageId,
            ["role"] = role,
            ["messageScope"] = messageScope,
        });

    public static JsonElement MessageTextDelta(string messageId, string delta)
        => Serialize(new JsonObject
        {
            ["messageId"] = messageId,
            ["delta"] = delta,
        });

    public static JsonElement MessageTextCompleted(string messageId)
        => Serialize(new JsonObject { ["messageId"] = messageId });

    public static JsonElement ReasoningStarted(string reasoningId, string? messageId = null)
    {
        var body = new JsonObject { ["reasoningId"] = reasoningId };
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            body["messageId"] = messageId;
        }

        return Serialize(body);
    }

    public static JsonElement ReasoningDelta(string reasoningId, string delta, string? messageId = null)
    {
        var body = new JsonObject
        {
            ["reasoningId"] = reasoningId,
            ["delta"] = delta,
        };
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            body["messageId"] = messageId;
        }

        return Serialize(body);
    }

    public static JsonElement ReasoningCompleted(string reasoningId, string? messageId = null)
    {
        var body = new JsonObject { ["reasoningId"] = reasoningId };
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            body["messageId"] = messageId;
        }

        return Serialize(body);
    }

    public static JsonElement ToolCallStarted(
        string toolCallId,
        string toolName,
        JsonElement args,
        string executor = "backend")
        => Serialize(new JsonObject
        {
            ["toolCallId"] = toolCallId,
            ["toolName"] = toolName,
            ["executor"] = executor,
            ["args"] = JsonNode.Parse(args.GetRawText()),
        });

    public static JsonElement ToolCallFinished(
        string toolCallId,
        string outcome,
        JsonElement? response = null,
        string? code = null,
        string? message = null)
    {
        var body = new JsonObject
        {
            ["toolCallId"] = toolCallId,
            ["outcome"] = outcome,
        };
        if (response.HasValue)
        {
            body["response"] = JsonNode.Parse(response.Value.GetRawText());
        }
        else
        {
            body["response"] = null;
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            body["code"] = code;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            body["message"] = message;
        }

        return Serialize(body);
    }

    public static JsonElement InterruptRequested(
        string interruptId,
        string waitId,
        string kind,
        IReadOnlyList<string>? actions = null,
        string? toolCallId = null,
        JsonElement? input = null,
        string? scope = null)
    {
        var body = new JsonObject
        {
            ["interruptId"] = interruptId,
            ["waitId"] = waitId,
            ["kind"] = kind,
        };
        if (!string.IsNullOrWhiteSpace(scope))
        {
            body["scope"] = scope;
        }

        if (actions is { Count: > 0 })
        {
            body["actions"] = new JsonArray(actions.Select(static action => (JsonNode?)action).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            body["toolCallId"] = toolCallId;
        }

        if (input is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
        {
            body["input"] = JsonNode.Parse(input.Value.GetRawText());
        }

        return Serialize(body);
    }

    public static JsonElement RunPaused(
        string waitId,
        string reason,
        string? interruptId = null,
        string resumeMode = "command")
    {
        var body = new JsonObject
        {
            ["waitId"] = waitId,
            ["reason"] = reason,
            ["resumeMode"] = resumeMode,
        };
        if (!string.IsNullOrWhiteSpace(interruptId))
        {
            body["interruptId"] = interruptId;
        }

        return Serialize(body);
    }

    public static JsonElement InterruptResolved(
        string interruptId,
        string waitId,
        string decision,
        JsonObject? actor = null,
        JsonArray? members = null)
    {
        var body = new JsonObject
        {
            ["interruptId"] = interruptId,
            ["waitId"] = waitId,
            ["decision"] = decision,
        };
        if (actor is { Count: > 0 })
        {
            body["actor"] = actor;
        }

        if (members is { Count: > 0 })
        {
            body["members"] = members;
        }

        return Serialize(body);
    }

    public static string ApprovalWaitId(string approvalRequestId) => $"wait:{approvalRequestId}";

    public static JsonElement RunResumed(string waitId)
        => Serialize(new JsonObject { ["waitId"] = waitId });

    public static JsonElement RunStopRequested(string targetRunId, string? actor = null, string? channel = null)
    {
        var body = new JsonObject { ["targetRunId"] = targetRunId };
        if (!string.IsNullOrWhiteSpace(actor))
        {
            body["actor"] = actor;
        }

        if (!string.IsNullOrWhiteSpace(channel))
        {
            body["channel"] = channel;
        }

        return Serialize(body);
    }

    /// <param name="usage">Optional run token usage.</param>
    /// <param name="response">
    /// Final assistant reply when known at terminal publish. Channel mirrors read this for hitl
    /// one-shot delivery when text was not accumulated from deltas (pre-Narad StreamDone carried
    /// <c>Result.Response</c> the same way).
    /// </param>
    public static JsonElement RunFinished(JsonElement? usage = null, string? response = null)
    {
        var body = new JsonObject();
        if (usage.HasValue)
        {
            body["usage"] = JsonNode.Parse(usage.Value.GetRawText());
        }

        if (!string.IsNullOrWhiteSpace(response))
        {
            // Top-level only — never nest GatewayChatResult-shaped `result` bags.
            body["response"] = response;
        }

        return Serialize(body);
    }

    public static JsonElement RunError(string code, string message, JsonElement? usage = null)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (usage.HasValue)
        {
            body["usage"] = JsonNode.Parse(usage.Value.GetRawText());
        }

        return Serialize(body);
    }

    public static JsonElement RunCancelled(string code, string message, JsonElement? usage = null)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (usage.HasValue)
        {
            body["usage"] = JsonNode.Parse(usage.Value.GetRawText());
        }

        return Serialize(body);
    }

    public static JsonElement ProgressRecorded(
        string progressId,
        string category,
        string label,
        string status,
        string privacy = "public",
        string? detail = null,
        JsonElement? usage = null)
    {
        var body = new JsonObject
        {
            ["progressId"] = progressId,
            ["category"] = category,
            ["label"] = label,
            ["status"] = status,
            ["privacy"] = privacy,
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body["detail"] = detail;
        }

        if (usage.HasValue)
        {
            body["usage"] = JsonNode.Parse(usage.Value.GetRawText());
        }

        return Serialize(body);
    }

    public static JsonElement TraceEvent(string name, JsonElement? data = null)
    {
        var body = new JsonObject { ["name"] = name };
        if (data.HasValue)
        {
            body["data"] = JsonNode.Parse(data.Value.GetRawText());
        }

        return Serialize(body);
    }

    public static JsonElement Usage(long promptTokens, long completionTokens, long? elapsedMs = null)
    {
        var body = new JsonObject
        {
            ["promptTokens"] = promptTokens,
            ["completionTokens"] = completionTokens,
            ["totalTokens"] = promptTokens + completionTokens,
        };
        if (elapsedMs is > 0)
        {
            body["elapsedMs"] = elapsedMs.Value;
        }

        return Serialize(body);
    }

    /// <summary>
    /// Stamps owner userId for internal approval-invalidation routing.
    /// Public Narad feeds redact this field (see NaradPublicAuthorityFilter).
    /// </summary>
    public static JsonElement WithOwnerUserId(JsonElement body, string? userId)
        => StampStringField(body, "userId", userId);

    /// <summary>
    /// Stamps authoring agent id for surface routing (channel projection, AAAT isolation).
    /// Public streams keep this field.
    /// </summary>
    public static JsonElement WithAgentId(JsonElement body, string? agentId)
        => StampStringField(body, "agentId", agentId);

    /// <summary>
    /// Stamps admit surface for user messages (<c>telegram</c>, <c>chat</c>, <c>agent_tool</c>, …).
    /// Channel projectors use this instead of session-originator heuristics.
    /// </summary>
    public static JsonElement WithSourceChannel(JsonElement body, string? sourceChannel)
        => StampStringField(body, "sourceChannel", sourceChannel);

    private static JsonElement StampStringField(JsonElement body, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return body;
        }

        JsonObject envelope = body.ValueKind == JsonValueKind.Object
            ? (JsonNode.Parse(body.GetRawText()) as JsonObject ?? new JsonObject())
            : new JsonObject();
        if (envelope[fieldName] is JsonValue existing
            && existing.GetValueKind() == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(existing.GetValue<string>()))
        {
            return body;
        }

        envelope[fieldName] = value.Trim();
        return Serialize(envelope);
    }

    private static JsonElement Serialize(JsonObject body)
    {
        using JsonDocument document = JsonDocument.Parse(body.ToJsonString());
        return document.RootElement.Clone();
    }
}
