using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<OutboxExecutionResult> ExecuteEditSemanticMessageAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        ChannelSemanticMessageAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransportMessageId))
            return new OutboxExecutionResult(false, "message_id_required", null, PermanentFailure: true);
        JsonObject body = ChannelSemanticRendering.RenderTelegram(payload);
        body.Remove("reply_parameters");
        body[messageIdProperty] = ParseTelegramNumberOrString(payload.TransportMessageId);
        // editMessageText accepts only InlineKeyboardMarkup for reply_markup. RenderTelegram
        // adds the persistent ReplyKeyboardMarkup used on new messages, which Telegram rejects
        // on edits and which left resolved approval cards showing their stale action buttons.
        // The persistent chat keyboard remains visible; this empty inline keyboard clears any
        // message-scoped inline actions from the edited card.
        body["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() };
        TelegramMessageResult result = await SendTelegramMessageForResultAsync(registration, body, cancellationToken,
            method: "editMessageText").ConfigureAwait(false);
        if (TelegramErrorContains(result.Execution.Error, "message is not modified"))
            return new OutboxExecutionResult(true, null, null, payload.TransportMessageId);
        if (TelegramErrorContains(result.Execution.Error, "message can't be edited"))
        {
            // Older Kautuka builds attached ReplyKeyboardMarkup to every message. The Bot API
            // refuses to edit those messages even when this edit request supplies a valid
            // inline keyboard. Retire the legacy card and replace it with the resolved rich
            // card so existing approvals do not remain actionable-looking forever.
            JsonObject deleteBody = new()
            {
                [chatIdProperty] = payload.ConversationId,
                [messageIdProperty] = ParseTelegramNumberOrString(payload.TransportMessageId),
            };
            OutboxExecutionResult deleted = await SendTelegramRequestAsync(
                registration, "deleteMessage", deleteBody, cancellationToken).ConfigureAwait(false);
            if (!deleted.Success) return deleted;

            JsonObject replacement = ChannelSemanticRendering.RenderTelegram(payload with
            {
                TransportMessageId = null,
                ReplyToMessageId = null,
            });
            TelegramMessageResult sent = await SendTelegramMessageForResultAsync(
                registration, replacement, cancellationToken, method: "sendRichMessage").ConfigureAwait(false);
            return sent.Execution with { TransportMessageId = sent.MessageId };
        }
        if (TelegramErrorContains(result.Execution.Error, "message to edit not found"))
            return new OutboxExecutionResult(false, "message_not_found", null, PermanentFailure: true);
        return result.Execution with { TransportMessageId = payload.TransportMessageId };
    }

    private static bool TelegramErrorContains(string? error, string description)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(error);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error_code", out JsonElement code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out int value) && value == 400
                && document.RootElement.TryGetProperty("description", out JsonElement text) && text.ValueKind == JsonValueKind.String
                && text.GetString()?.Contains(description, StringComparison.OrdinalIgnoreCase) is true;
        }
        catch (JsonException) { return false; }
    }
}
