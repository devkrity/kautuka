using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<OutboxExecutionResult> ExecuteSendTextAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelTextMessageAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelTextMessageAction);
        return payload is null
            ? new OutboxExecutionResult(true, null, null)
            : await SendTelegramRequestAsync(registration, "sendMessage", BuildTextPayload(payload.ConversationId, payload.ConversationThreadId, payload.Text, payload.ParseMode, payload.SenderDisplayName), cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteSendSemanticMessageAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelSemanticMessageAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        if (payload is null) return new OutboxExecutionResult(true, null, null);
        TelegramMessageResult result = await SendTelegramMessageForResultAsync(
            registration,
            ChannelSemanticRendering.RenderTelegram(payload),
            cancellationToken,
            method: "sendRichMessage").ConfigureAwait(false);
        return result.Execution with { TransportMessageId = result.MessageId };
    }

    private async Task<OutboxExecutionResult> ExecuteSendChatActionAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelChatAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelChatAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        JsonObject body = new()
        {
            [chatIdProperty] = payload.ConversationId,
            ["action"] = payload.Action,
        };
        return await SendTelegramRequestAsync(registration, "sendChatAction", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteSendInlineKeyboardAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelInlineKeyboardAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelInlineKeyboardAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        return await SendTelegramRequestAsync(
            registration,
            "sendMessage",
            BuildInlineKeyboardPayload(payload.ConversationId, payload.ConversationThreadId, payload.Text, payload.Rows, payload.ParseMode),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteSendInteractionPromptAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelInteractionPromptAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelInteractionPromptAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        TelegramMessageResult result = await SendTelegramMessageForResultAsync(
            registration,
            BuildInlineKeyboardPayload(payload.ConversationId, payload.ConversationThreadId, payload.Prompt, payload.Rows, payload.ParseMode, payload.SenderDisplayName),
            cancellationToken).ConfigureAwait(false);

        if (!result.Execution.Success)
        {
            return result.Execution;
        }

        // Return TransportMessageId so Gateway can update interaction state.
        return result.Execution with { TransportMessageId = result.MessageId };
    }

    private async Task<OutboxExecutionResult> ExecuteAcknowledgeCallbackAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelCallbackAckAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelCallbackAckAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        JsonObject body = new()
        {
            ["callback_query_id"] = payload.AcknowledgeToken,
            ["text"] = payload.Text,
        };
        return await SendCallbackAcknowledgementAsync(registration, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteEditMessageTextAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelMessageEditAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelMessageEditAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        JsonObject body = new()
        {
            [chatIdProperty] = payload.ConversationId,
            [messageIdProperty] = ParseTelegramNumberOrString(payload.MessageId),
        };

        (string resolvedText, string? resolvedParseMode) = ApplyTelegramAttribution(payload.Text, payload.ParseMode, payload.SenderDisplayName);
        body["text"] = resolvedText;

        if (!string.IsNullOrWhiteSpace(resolvedParseMode))
        {
            body["parse_mode"] = resolvedParseMode;
        }

        if (payload.ClearInlineKeyboard)
        {
            body["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() };
        }

        return await SendTelegramRequestAsync(registration, "editMessageText", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteDeleteMessageAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelMessageDeleteAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelMessageDeleteAction);
        if (payload is null)
        {
            return new OutboxExecutionResult(true, null, null);
        }

        JsonObject body = new()
        {
            [chatIdProperty] = payload.ConversationId,
            [messageIdProperty] = ParseTelegramNumberOrString(payload.MessageId),
        };
        return await SendTelegramRequestAsync(registration, "deleteMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteAddReactionAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelReactionAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelReactionAction);
        if (payload is null) return new OutboxExecutionResult(true, null, null);
        JsonObject body = new()
        {
            [chatIdProperty] = payload.ConversationId,
            [messageIdProperty] = ParseTelegramNumberOrString(payload.MessageId),
            ["reaction"] = new JsonArray((JsonNode?)new JsonObject { ["type"] = "emoji", ["emoji"] = payload.Reaction }),
        };
        return await SendTelegramRequestAsync(registration, "setMessageReaction", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxExecutionResult> ExecuteStreamMessageAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelStreamMessageAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelStreamMessageAction);
        if (payload is null || payload.Sequence < 0) return new OutboxExecutionResult(true, null, null);
        ChannelStreamDelivery? existing = attachmentStore.GetStreamDelivery(registration.RegistrationId, payload.StreamId);
        if (existing is not null && payload.Sequence <= existing.LastSequence) return new OutboxExecutionResult(true, null, null, existing.TransportMessageId);
        string? transportMessageId = payload.TransportMessageId ?? existing?.TransportMessageId;
        if (string.IsNullOrWhiteSpace(transportMessageId))
        {
            TelegramMessageResult sent = await SendTelegramMessageForResultAsync(
                registration,
                BuildRichTextPayload(payload.ConversationId, payload.ConversationThreadId, payload.AccumulatedText, payload.SenderDisplayName),
                cancellationToken,
                method: "sendRichMessage").ConfigureAwait(false);
            if (sent.Execution.Success && !string.IsNullOrWhiteSpace(sent.MessageId)) attachmentStore.RecordStreamDelivery(registration.RegistrationId, payload.StreamId, sent.MessageId, payload.Sequence, payload.IsFinal);
            return sent.Execution with { TransportMessageId = sent.MessageId };
        }
        JsonObject body = BuildRichTextPayload(payload.ConversationId, payload.ConversationThreadId, payload.AccumulatedText, payload.SenderDisplayName);
        body[messageIdProperty] = ParseTelegramNumberOrString(transportMessageId);
        OutboxExecutionResult result = await SendTelegramRequestAsync(registration, "editMessageText", body, cancellationToken).ConfigureAwait(false);
        if (result.Success) attachmentStore.RecordStreamDelivery(registration.RegistrationId, payload.StreamId, transportMessageId, payload.Sequence, payload.IsFinal);
        return result with { TransportMessageId = transportMessageId };
    }

    private async Task<OutboxExecutionResult> ExecuteSendFileAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelOutboundFileAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelOutboundFileAction);
        if (payload is null) return new OutboxExecutionResult(true, null, null);
        using ChannelOutboundAssetLease? lease = outboundAssets.OpenAvailable(payload.UploadId, registration.RegistrationId, payload.ConversationId);
        if (lease is null || lease.SizeBytes != payload.DeclaredSizeBytes || !string.Equals(lease.SafeFileName, payload.SafeFileName, StringComparison.Ordinal) || !string.Equals(lease.ContentType, payload.ContentType, StringComparison.OrdinalIgnoreCase)) return new OutboxExecutionResult(false, "outbound_asset_unavailable_or_mismatched", null);
        using MultipartFormDataContent multipart = new();
        using StringContent chatContent = new(payload.ConversationId);
        multipart.Add(chatContent, "chat_id");
        using StringContent? captionContent = string.IsNullOrWhiteSpace(payload.Caption) ? null : new StringContent(payload.Caption);
        if (captionContent is not null) multipart.Add(captionContent, "caption");
        using StreamContent file = new(lease.Content); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(lease.ContentType);
        multipart.Add(file, "document", lease.SafeFileName);
        using HttpResponseMessage response = await httpClientFactory.CreateClient("telegram-api").PostAsync(new Uri($"https://api.telegram.org/bot{registration.BotToken}/sendDocument"), multipart, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new OutboxExecutionResult(false, $"http_{(int)response.StatusCode}", 30);
        using JsonDocument result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        string messageId = result.RootElement.TryGetProperty("result", out JsonElement resultElement) && resultElement.TryGetProperty("message_id", out JsonElement id) ? id.ToString() : string.Empty;
        lease.Dispose();
        outboundAssets.MarkConsumed(payload.UploadId);
        return new OutboxExecutionResult(true, null, null, messageId);
    }

}
