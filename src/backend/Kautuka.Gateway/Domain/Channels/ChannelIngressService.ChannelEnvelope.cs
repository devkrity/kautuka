using System.Text.Json;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private static ChannelInboundEnvelope? CreateChannelEnvelope(ChannelInboundEvent evt)
    {
        bool isCallbackEvent = string.Equals(evt.EventType, "callback", StringComparison.OrdinalIgnoreCase);

        if (isCallbackEvent)
        {
            if (string.IsNullOrWhiteSpace(evt.AcknowledgeToken)
                || string.IsNullOrWhiteSpace(evt.ExternalUserId)
                || string.IsNullOrWhiteSpace(evt.ConversationId))
            {
                return null;
            }

            string threadId = string.IsNullOrWhiteSpace(evt.ConversationThreadId)
                ? "root"
                : evt.ConversationThreadId;
            string? messageThreadId = string.Equals(evt.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
                && string.Equals(threadId, "root", StringComparison.Ordinal)
                    ? null
                    : threadId;

            return new ChannelInboundEnvelope(
                UpdateId: evt.EventId,
                ExternalUserId: evt.ExternalUserId,
                ChatId: evt.ConversationId,
                ProviderThreadId: threadId,
                MessageThreadId: messageThreadId,
                MessageText: evt.CallbackData ?? string.Empty,
                IsCallback: true,
                CallbackAction: ParseCallbackAction(evt.CallbackData),
                CallbackInteractionId: ParseCallbackInteractionId(evt.CallbackData),
                CallbackQueryId: evt.AcknowledgeToken,
                CallbackMessageId: evt.SourceMessageId,
                InboundMedia: evt.InboundMedia ?? [],
                CallbackTransportMessageId: evt.SourceTransportMessageId);
        }

        bool isMessageEvent = string.Equals(evt.EventType, "message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.EventType, "command", StringComparison.OrdinalIgnoreCase);

        if (!isMessageEvent
            || string.IsNullOrWhiteSpace(evt.ExternalUserId)
            || string.IsNullOrWhiteSpace(evt.ConversationId))
        {
            return null;
        }

        string providerThreadId = string.IsNullOrWhiteSpace(evt.ConversationThreadId)
            ? evt.ConversationId
            : evt.ConversationThreadId;

        if (ChannelCommandParser.IsCommand(evt.Text ?? string.Empty, "/task"))
        {
            providerThreadId = $"task:{evt.EventId}";
        }

        return new ChannelInboundEnvelope(
            UpdateId: evt.EventId,
            ExternalUserId: evt.ExternalUserId,
            ChatId: evt.ConversationId,
            ProviderThreadId: providerThreadId,
            MessageThreadId: evt.ConversationThreadId,
            MessageText: evt.Text ?? string.Empty,
            IsCallback: false,
            CallbackAction: null,
            CallbackInteractionId: null,
                CallbackQueryId: null,
                CallbackMessageId: null,
                InboundMedia: evt.InboundMedia ?? [],
            PromptTransportMessageId: evt.SourceTransportMessageId ?? evt.SourceMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReplySourceMessageId: evt.ReplySourceTransportMessageId);
    }

    private static string ParseCallbackAction(string? callbackData)
    {
        if (string.IsNullOrWhiteSpace(callbackData))
        {
            return "unknown";
        }

        if (TryParseStructuredApprovalCallback(callbackData, out string? interactionId, out string? decision))
        {
            _ = interactionId;
            return string.IsNullOrWhiteSpace(decision) ? "unknown" : decision.ToUpperInvariant();
        }

        string[] parts = callbackData.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 && string.Equals(parts[0], "hitl", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2].ToUpperInvariant();
        }

        return callbackData.Trim();
    }

    private static string? ParseCallbackInteractionId(string? callbackData)
    {
        if (string.IsNullOrWhiteSpace(callbackData))
        {
            return null;
        }

        if (TryParseStructuredApprovalCallback(callbackData, out string? interactionId, out string? decision))
        {
            _ = decision;
            return string.IsNullOrWhiteSpace(interactionId) ? null : interactionId;
        }

        string[] parts = callbackData.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 && string.Equals(parts[0], "hitl", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        return null;
    }

    /// <summary>
    /// Structured callback payloads: <c>{"kind":"approval.resolve","interactionId":"...","decision":"..."}</c>.
    /// Legacy <c>hitl:{interactionId}:{decision}</c> tokens remain supported.
    /// </summary>
    private static bool TryParseStructuredApprovalCallback(string callbackData, out string? interactionId, out string? decision)
    {
        interactionId = null;
        decision = null;
        string trimmed = callbackData.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? kind = root.TryGetProperty("kind", out JsonElement kindElement)
                ? kindElement.GetString()
                : null;
            if (!string.Equals(kind, "approval.resolve", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            interactionId = root.TryGetProperty("interactionId", out JsonElement idElement)
                ? idElement.GetString()?.Trim()
                : null;
            decision = root.TryGetProperty("decision", out JsonElement decisionElement)
                ? decisionElement.GetString()?.Trim()
                : null;
            return !string.IsNullOrWhiteSpace(interactionId) || !string.IsNullOrWhiteSpace(decision);
        }
        catch (JsonException)
        {
            return false;
        }
    }

}

internal sealed record ChannelInboundEnvelope(
    string UpdateId,
    string ExternalUserId,
    string ChatId,
    string ProviderThreadId,
    string? MessageThreadId,
    string MessageText,
    bool IsCallback,
    string? CallbackAction,
    string? CallbackInteractionId,
    string? CallbackQueryId,
    long? CallbackMessageId,
    IReadOnlyList<ChannelInboundMediaDescriptor> InboundMedia,
    string? CallbackTransportMessageId = null,
    string? PromptTransportMessageId = null,
    string? ReplySourceMessageId = null);
