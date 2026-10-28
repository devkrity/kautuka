using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Kautuka.ChannelHost.Hosting;

/// <summary>
/// Both modes echo the originating user prompt. Hitl adds approvals + one terminal
/// reply on <c>run.finished</c>; mirror adds one-shot completed assistant messages.
/// Neither mode emits progressive <c>stream_message</c> edits to providers.
/// </summary>
internal enum ChannelHostHitlTetherMode
{
    Hitl = 0,
    Mirror = 1,
}

internal static class ChannelHostHitlTetherModeParser
{
    public const string SettingsKey = "hitl_tether_mode";

    public static ChannelHostHitlTetherMode Parse(string? value)
#pragma warning disable CA1308 // ToLowerInvariant is intentional for switch-based parsing of mode strings
        => value?.Trim().ToLowerInvariant() switch
#pragma warning restore CA1308
        {
            "hitl" => ChannelHostHitlTetherMode.Hitl,
            _ => ChannelHostHitlTetherMode.Mirror,
        };
}

internal static class ChannelHostRuntimeEventTypes
{
    public const string UserMessageRecorded = "user.message.recorded";
    public const string AssistantTextDelta = "assistant.text.delta";
    public const string AssistantMessageCompleted = "assistant.message.completed";
    public const string RunFinished = "run.finished";
    public const string ToolApprovalRequested = "tool.approval.requested";
    public const string ToolApprovalResolved = "tool.approval.resolved";
}

internal sealed record ChannelTranslatedOutboundAction(string ActionType, string PayloadJson);

/// <summary>
/// Shared translator for every ChannelHost provider (Telegram outbox and Slack gRPC).
/// Maps Gateway <c>runtime_event</c> envelopes to transport actions by <c>hitl_tether_mode</c>.
/// Never emits progressive <c>stream_message</c> edits; completed text is one-shot semantic delivery.
/// </summary>
internal sealed class ChannelRuntimeEventTranslator
{
    public static ChannelRuntimeEventTranslator Shared { get; } = new();

    private readonly ConcurrentDictionary<string, StringBuilder> textByMessage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> emittedFinalByRun = new(StringComparer.Ordinal);

    public IReadOnlyList<ChannelTranslatedOutboundAction> Translate(
        JsonElement attachmentSettings,
        ChannelRuntimeEventAction runtimeEvent,
        string? approvalTransportMessageId = null,
        bool richContext = false)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        ChannelHostHitlTetherMode mode = ChannelHostHitlTetherModeParser.Parse(
            ReadSetting(attachmentSettings, ChannelHostHitlTetherModeParser.SettingsKey));
        if (!IsAllowed(runtimeEvent.EventType))
            return [];

        IReadOnlyList<ChannelTranslatedOutboundAction> translated = runtimeEvent.EventType switch
        {
            ChannelHostRuntimeEventTypes.UserMessageRecorded => TranslateUserMessage(runtimeEvent, mode),
            ChannelHostRuntimeEventTypes.AssistantTextDelta => TranslateTextDelta(runtimeEvent, mode),
            ChannelHostRuntimeEventTypes.AssistantMessageCompleted => TranslateMessageCompleted(runtimeEvent, mode),
            ChannelHostRuntimeEventTypes.RunFinished => TranslateRunFinished(runtimeEvent),
            ChannelHostRuntimeEventTypes.ToolApprovalRequested => TranslateApprovalRequested(runtimeEvent, richContext),
            ChannelHostRuntimeEventTypes.ToolApprovalResolved => TranslateApprovalResolved(runtimeEvent, approvalTransportMessageId, richContext),
            _ => [],
        };
        return translated.Select(action =>
        {
            if (action.ActionType is not (ChannelOutboundActionTypes.SendSemanticMessage or ChannelOutboundActionTypes.EditSemanticMessage)) return action;
            ChannelSemanticMessageAction? message = JsonSerializer.Deserialize(action.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
            bool workflowReview = runtimeEvent.EventType is ChannelHostRuntimeEventTypes.ToolApprovalRequested
                or ChannelHostRuntimeEventTypes.ToolApprovalResolved
                && TryReadPayload(runtimeEvent.PayloadJson, out JsonElement approvalPayload)
                && IsWorkflowReview(approvalPayload);
            ChannelSemanticMessageDocument? document = message is null
                ? null
                : workflowReview
                    ? message.Document
                    : ChannelMessageTemplates.WithSession(
                        message.Document, runtimeEvent.SessionDisplayName ?? (richContext ? "Untitled session" : null));
            if (document is not null && richContext
                && runtimeEvent.EventType != ChannelHostRuntimeEventTypes.UserMessageRecorded
                && !workflowReview)
                document = ChannelMessageTemplates.WithPrompt(document, runtimeEvent.PromptText);
            return message is null ? action : action with
            {
                PayloadJson = JsonSerializer.Serialize(message with
                {
                    Document = document!,
                    // Rich semantic content does not remove the provider reply anchor.
                    // ChannelHost owns how each provider renders it; keep the normalized
                    // source message id available to that provider.
                    ReplyToMessageId = runtimeEvent.ReplyToMessageId,
                },
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)
            };
        }).ToArray();
    }

    internal static bool IsAllowed(string eventType)
        => eventType is ChannelHostRuntimeEventTypes.ToolApprovalRequested
            or ChannelHostRuntimeEventTypes.ToolApprovalResolved
            or ChannelHostRuntimeEventTypes.UserMessageRecorded
            or ChannelHostRuntimeEventTypes.AssistantMessageCompleted
            or ChannelHostRuntimeEventTypes.RunFinished
            // Deltas are buffered in-process for one-shot completed messages; never streamed out.
            or ChannelHostRuntimeEventTypes.AssistantTextDelta;

    private static IReadOnlyList<ChannelTranslatedOutboundAction> TranslateUserMessage(
        ChannelRuntimeEventAction runtimeEvent,
        ChannelHostHitlTetherMode mode)
    {
        // Both modes echo the originating prompt so approvals and finals read in context.
        _ = mode;
        string? conversationId = RequireConversationId(runtimeEvent);
        if (conversationId is null)
            return [];
        if (!TryReadPayload(runtimeEvent.PayloadJson, out JsonElement payload))
            return [];

        string? text = ReadString(payload, "message") ?? ReadString(payload, "content");
        if (string.IsNullOrWhiteSpace(text))
            text = "Message with attachments";

        // Attribute which surface agent the prompt is aimed at (browser→channel mirror).
        return
        [
            Semantic(
                conversationId,
                runtimeEvent.ConversationThreadId,
                ChannelMessageTemplates.UserMessage(text, SenderLabel(runtimeEvent)),
                runtimeEvent.InteractionId),
        ];
    }

    private IReadOnlyList<ChannelTranslatedOutboundAction> TranslateTextDelta(
        ChannelRuntimeEventAction runtimeEvent,
        ChannelHostHitlTetherMode mode)
    {
        _ = mode;
        if (!TryReadPayload(runtimeEvent.PayloadJson, out JsonElement payload))
            return [];

        string? messageId = ReadString(payload, "messageId");
        string? delta = ReadString(payload, "delta");
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(delta))
            return [];
        if (payload.TryGetProperty("isThinking", out JsonElement thinking) && thinking.ValueKind == JsonValueKind.True)
            return [];

        string key = MessageKey(runtimeEvent, messageId);
        StringBuilder builder = textByMessage.GetOrAdd(key, static _ => new StringBuilder());
        lock (builder)
            builder.Append(delta);

        // Channels never progressive-stream tokens to providers.
        return [];
    }

    private IReadOnlyList<ChannelTranslatedOutboundAction> TranslateMessageCompleted(
        ChannelRuntimeEventAction runtimeEvent,
        ChannelHostHitlTetherMode mode)
    {
        if (!TryReadPayload(runtimeEvent.PayloadJson, out JsonElement payload))
            return [];

        string? messageId = ReadString(payload, "messageId");
        if (string.IsNullOrEmpty(messageId))
            return [];

        // Prefer wire content stamped by Gateway (durable). In-proc buffer is a fallback only.
        string? content = ReadString(payload, "content")
            ?? ReadString(payload, "text")
            ?? ReadString(payload, "message");
        string key = MessageKey(runtimeEvent, messageId);
        if (textByMessage.TryRemove(key, out StringBuilder? builder))
        {
            string buffered;
            lock (builder)
                buffered = builder.ToString();
            if (string.IsNullOrWhiteSpace(content))
                content = buffered;
        }

        // HITL finals are run.finished only so pause-time completed text cannot steal the reply.
        if (mode == ChannelHostHitlTetherMode.Hitl)
            return [];

        if (string.IsNullOrWhiteSpace(content))
            return [];

        string? conversationId = RequireConversationId(runtimeEvent);
        if (conversationId is null)
            return [];

        // One final agent_response per root — do not also emit after run.finished stamped text.
        string finalKey = FinalKey(runtimeEvent);
        if (!emittedFinalByRun.TryAdd(finalKey, 0))
            return [];

        return
        [
            Semantic(
                conversationId,
                runtimeEvent.ConversationThreadId,
                ChannelMessageTemplates.AgentResponse(content, SenderLabel(runtimeEvent)),
                runtimeEvent.InteractionId),
        ];
    }

    private IReadOnlyList<ChannelTranslatedOutboundAction> TranslateRunFinished(ChannelRuntimeEventAction runtimeEvent)
    {
        string? conversationId = RequireConversationId(runtimeEvent);
        if (conversationId is null)
            return [];

        string? response = TryReadRunFinishedResponse(runtimeEvent.PayloadJson);
        if (string.IsNullOrWhiteSpace(response))
            return [];

        // Reserve only when we have text to deliver.
        if (!emittedFinalByRun.TryAdd(FinalKey(runtimeEvent), 0))
            return [];

        return
        [
            Semantic(
                conversationId,
                runtimeEvent.ConversationThreadId,
                ChannelMessageTemplates.AgentResponse(response, SenderLabel(runtimeEvent)),
                runtimeEvent.InteractionId),
        ];
    }

    private static IReadOnlyList<ChannelTranslatedOutboundAction> TranslateApprovalRequested(ChannelRuntimeEventAction runtimeEvent, bool richContext)
    {
        string? conversationId = RequireConversationId(runtimeEvent);
        if (conversationId is null)
            return [];
        if (!TryReadPayload(runtimeEvent.PayloadJson, out JsonElement payload))
            return [];

        string requestId = ReadString(payload, "requestId")
            ?? ReadString(payload, "approvalRequestId")
            ?? runtimeEvent.EventId;
        string toolName = ReadString(payload, "toolName") ?? "tool";
        string tokenBase = string.IsNullOrWhiteSpace(runtimeEvent.InteractionId) ? requestId : runtimeEvent.InteractionId;
        string approveToken = BuildApprovalCallbackToken(tokenBase, "approve");
        string denyToken = BuildApprovalCallbackToken(tokenBase, "deny");
        // Prefer Gateway-built context (user utterance / args). Fall back to a compact tool ask.
        bool workflowReview = IsWorkflowReview(payload);
        string summary = ReadString(payload, "summary")
            ?? ReadString(payload, "prompt")
            ?? BuildApprovalPromptFallback(toolName, ReadString(payload, "userMessage"));
        string title = ReadString(payload, "title") ?? "Permission required";
        ChannelSemanticAction[] actions = ReadApprovalActions(payload, tokenBase);
        ChannelSemanticMessageDocument document = ChannelMessageTemplates.ApprovalRequest(title, summary, actions, SenderLabel(runtimeEvent));
        if (payload.TryGetProperty("fields", out JsonElement fieldsElement)
            && fieldsElement.ValueKind == JsonValueKind.Array)
        {
            ChannelSemanticField[] fields = fieldsElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Object)
                .Select(static item => new ChannelSemanticField(
                    ReadString(item, "label") ?? string.Empty,
                    ReadString(item, "value") ?? string.Empty))
                .Where(static field => !string.IsNullOrWhiteSpace(field.Label))
                .Take(10)
                .ToArray();
            var blocks = new List<ChannelSemanticBlock>
            {
                new(ChannelSemanticBlockTypes.Header, Text: title),
                new(ChannelSemanticBlockTypes.Section, Text: summary),
            };
            if (fields.Length > 0)
                blocks.Add(new(ChannelSemanticBlockTypes.Fields, Fields: fields));
            if (workflowReview)
                blocks.Add(new(ChannelSemanticBlockTypes.Context, Text: "Open Kautuka → Approvals for full details."));
            blocks.Add(new(ChannelSemanticBlockTypes.Actions, Actions: actions));
            document = document with { Blocks = blocks };
        }
        if (richContext && !workflowReview)
            document = ExpandApprovalDetails(document, BuildApprovalDetails(ReadString(payload, "argumentsJson")));
        return
        [
            Semantic(
                conversationId,
                runtimeEvent.ConversationThreadId,
                document,
                runtimeEvent.InteractionId ?? tokenBase),
        ];
    }

    private static string BuildApprovalPromptFallback(string toolName, string? userMessage)
    {
        string ask = $"Allow the agent to run `{toolName}`?";
        return string.IsNullOrWhiteSpace(userMessage) ? ask : $"{userMessage.Trim()}\n\n{ask}";
    }

    private static bool IsWorkflowReview(JsonElement payload)
        => string.Equals(ReadString(payload, "kind"), "workflow_review", StringComparison.Ordinal)
            || string.Equals(ReadString(payload, "toolType"), "workflow_review", StringComparison.Ordinal);

    private static ChannelSemanticAction[] ReadApprovalActions(JsonElement payload, string tokenBase)
    {
        if (payload.TryGetProperty("actions", out JsonElement actionsElement)
            && actionsElement.ValueKind == JsonValueKind.Array)
        {
            ChannelSemanticAction[] actions = actionsElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Object)
                .Select(item =>
                {
                    string value = ReadString(item, "value") ?? string.Empty;
                    string label = ReadString(item, "label") ?? value;
                    string style = ReadString(item, "style") ?? ChannelSemanticStyles.Default;
                    return new ChannelSemanticAction(value, ChannelSemanticActionTypes.Button, label,
                        BuildApprovalCallbackToken(tokenBase, value), style, Required: true);
                })
                .Where(static action => !string.IsNullOrWhiteSpace(action.CallbackToken)
                    && !string.IsNullOrWhiteSpace(action.Label))
                .ToArray();
            return actions;
        }

        return
        [
            new("approve", ChannelSemanticActionTypes.Button, "Approve",
                BuildApprovalCallbackToken(tokenBase, "approve"), ChannelSemanticStyles.Primary, Required: true),
            new("deny", ChannelSemanticActionTypes.Button, "Deny",
                BuildApprovalCallbackToken(tokenBase, "deny"), ChannelSemanticStyles.Danger, Required: true),
        ];
    }

    /// <summary>
    /// Prefers structured <c>approval.resolve</c> JSON when it fits Telegram's 64-byte
    /// callback_data limit; otherwise emits compact legacy <c>hitl:</c> tokens.
    /// Gateway admits both forms.
    /// </summary>
    internal static string BuildApprovalCallbackToken(string interactionId, string decision)
    {
        string structured = $"{{\"kind\":\"approval.resolve\",\"interactionId\":\"{EscapeJson(interactionId)}\",\"decision\":\"{EscapeJson(decision)}\"}}";
        if (Encoding.UTF8.GetByteCount(structured) <= 64)
            return structured;

        return $"hitl:{interactionId}:{decision}";
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static IReadOnlyList<ChannelTranslatedOutboundAction> TranslateApprovalResolved(
        ChannelRuntimeEventAction runtimeEvent,
        string? approvalTransportMessageId,
        bool richContext)
    {
        string? conversationId = RequireConversationId(runtimeEvent);
        if (conversationId is null)
            return [];
        if (!TryReadPayload(runtimeEvent.PayloadJson, out JsonElement payload))
            return [];

        string decision = ReadString(payload, "decision") ?? "resolved";
        string toolName = ReadString(payload, "toolName") ?? "tool";
        bool workflowReview = IsWorkflowReview(payload);
        if (workflowReview)
        {
            string outcome = decision switch
            {
                "approve" or "approved" => "Approved. The next step has started.",
                "request_changes" => "Changes requested. This step will run again.",
                "stop_run" or "stop" => "Stopped.",
                "expired" => "Expired with no decision.",
                _ => "Approval was resolved.",
            };
            string title = ReadString(payload, "title") ?? "Workflow review";
            ChannelSemanticMessageDocument workflowResolution = ChannelMessageTemplates.WorkflowReviewResolution(
                title, outcome, SenderLabel(runtimeEvent));
            string? workflowMessageId = approvalTransportMessageId
                ?? ReadString(payload, "transportMessageId")
                ?? ReadString(payload, "sourceTransportMessageId");
            return [new ChannelTranslatedOutboundAction(
                string.IsNullOrWhiteSpace(workflowMessageId)
                    ? ChannelOutboundActionTypes.SendSemanticMessage
                    : ChannelOutboundActionTypes.EditSemanticMessage,
                JsonSerializer.Serialize(new ChannelSemanticMessageAction(
                    conversationId,
                    runtimeEvent.ConversationThreadId,
                    workflowResolution,
                    runtimeEvent.InteractionId,
                    TransportMessageId: workflowMessageId),
                    ChannelContractsJsonContext.Default.ChannelSemanticMessageAction))];
        }
        string? detail = ReadString(payload, "reason") ?? ReadString(payload, "resolutionSource");
        string? transportMessageId = approvalTransportMessageId
            ?? ReadString(payload, "transportMessageId")
            ?? ReadString(payload, "sourceTransportMessageId");
        ChannelSemanticMessageDocument document = ChannelMessageTemplates.ApprovalResolution(
            $"Approval for {toolName}",
            decision,
            detail,
            SenderLabel(runtimeEvent));

        if (richContext)
        {
            document = ExpandApprovalDetails(document, BuildApprovalDetails(ReadString(payload, "argumentsJson")));
            return [new ChannelTranslatedOutboundAction(
                string.IsNullOrWhiteSpace(transportMessageId) ? ChannelOutboundActionTypes.SendSemanticMessage : ChannelOutboundActionTypes.EditSemanticMessage,
                JsonSerializer.Serialize(new ChannelSemanticMessageAction(conversationId, runtimeEvent.ConversationThreadId, document,
                    runtimeEvent.InteractionId, TransportMessageId: transportMessageId), ChannelContractsJsonContext.Default.ChannelSemanticMessageAction))];
        }

        if (!string.IsNullOrWhiteSpace(transportMessageId))
        {
            return
            [
                new ChannelTranslatedOutboundAction(
                    ChannelOutboundActionTypes.EditMessageText,
                    JsonSerializer.Serialize(
                        new ChannelMessageEditAction(
                            conversationId,
                            transportMessageId,
                            ChannelMessageTemplates.WithSession(document, runtimeEvent.SessionDisplayName).FallbackText ?? $"{toolName}: {decision}",
                            ClearInlineKeyboard: true,
                            SenderDisplayName: SenderLabel(runtimeEvent)),
                        ChannelContractsJsonContext.Default.ChannelMessageEditAction)),
            ];
        }

        return
        [
            Semantic(conversationId, runtimeEvent.ConversationThreadId, document, runtimeEvent.InteractionId),
        ];
    }

    private static ChannelSemanticMessageDocument ExpandApprovalDetails(ChannelSemanticMessageDocument document, string? arguments)
    {
        // Keep the approval text and originating prompt visible in their own blocks.
        // The expandable details are reserved for the tool arguments only.
        List<ChannelSemanticBlock> blocks =
        [.. document.Blocks.Where(block => block.Type != ChannelSemanticBlockTypes.Actions)];
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            blocks.Add(new(ChannelSemanticBlockTypes.Details, Text: "Request details", Expanded: false,
                Blocks: [new(ChannelSemanticBlockTypes.Section, Text: arguments)]));
        }
        blocks.AddRange(document.Blocks.Where(block => block.Type == ChannelSemanticBlockTypes.Actions));
        return document with { Blocks = blocks };
    }

    private static string? BuildApprovalDetails(string? argumentsJson)
    {
        const int maximumArgumentsCharacters = 1800;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        return argumentsJson.Length <= maximumArgumentsCharacters
            ? argumentsJson
            : argumentsJson[..(maximumArgumentsCharacters - 16)] + "\n… (truncated)";
    }

    /// <summary>User-facing attribution only — never raw agent ids.</summary>
    private static string SenderLabel(ChannelRuntimeEventAction runtimeEvent)
    {
        string name = !string.IsNullOrWhiteSpace(runtimeEvent.AgentDisplayName)
            ? runtimeEvent.AgentDisplayName.Trim()
            : "Assistant";
        string identity = string.IsNullOrWhiteSpace(runtimeEvent.AgentIcon) ? name : $"{runtimeEvent.AgentIcon} {name}";
        return identity;
    }

    /// <summary>
    /// Prefer root run identity for one-shot final dedupe; fall back to run id so empty strings
    /// do not collapse unrelated sessions together.
    /// </summary>
    private static string FinalKey(ChannelRuntimeEventAction runtimeEvent)
    {
        // The translator is process-wide, but Gateway fans the same run out to every
        // linked channel registration. Final deduplication must be scoped to that
        // registration or whichever channel is translated first suppresses the others.
        string registrationId = runtimeEvent.RegistrationId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(runtimeEvent.RootRunId))
            return $"{registrationId}:{runtimeEvent.RootRunId}";
        if (!string.IsNullOrWhiteSpace(runtimeEvent.RunId))
            return $"{registrationId}:{runtimeEvent.RunId}";
        return $"{registrationId}:{runtimeEvent.SessionId}:{runtimeEvent.EventId}";
    }

    private static ChannelTranslatedOutboundAction Semantic(
        string conversationId,
        string? conversationThreadId,
        ChannelSemanticMessageDocument document,
        string? interactionId)
        => new(
            ChannelOutboundActionTypes.SendSemanticMessage,
            JsonSerializer.Serialize(
                new ChannelSemanticMessageAction(conversationId, conversationThreadId, document, interactionId),
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction));

    private static string? RequireConversationId(ChannelRuntimeEventAction runtimeEvent)
        => string.IsNullOrWhiteSpace(runtimeEvent.ConversationId) ? null : runtimeEvent.ConversationId;

    private static string MessageKey(ChannelRuntimeEventAction runtimeEvent, string messageId)
        => $"{runtimeEvent.RegistrationId}:{runtimeEvent.RootRunId}:{messageId}";

    private static string? ReadSetting(JsonElement settings, string key)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(key, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static bool TryReadPayload(string payloadJson, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryReadRunFinishedResponse(string payloadJson)
    {
        if (!TryReadPayload(payloadJson, out JsonElement payload))
            return null;

        if (payload.TryGetProperty("result", out JsonElement result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.String)
            return nested.GetString();

        return ReadString(payload, "response");
    }
}
