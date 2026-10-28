using System.Globalization;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private static JsonObject BuildTextPayload(string conversationId, string? conversationThreadId, string text, string? parseMode = null, string? senderDisplayName = null)
    {
        _ = conversationThreadId; // Telegram topic routing is retired; every message targets the private chat root.
        (string resolvedText, string? resolvedParseMode) = ApplyTelegramAttribution(text, parseMode, senderDisplayName);

        JsonObject body = new()
        {
            [chatIdProperty] = conversationId,
            ["text"] = resolvedText,
            ["reply_markup"] = ChannelSemanticRendering.CreateTelegramReplyKeyboardMarkup(),
        };

        if (!string.IsNullOrWhiteSpace(resolvedParseMode))
        {
            body["parse_mode"] = resolvedParseMode;
        }

        return body;
    }

    private static JsonObject BuildRichTextPayload(string conversationId, string? conversationThreadId, string text, string? senderDisplayName)
    {
        _ = conversationThreadId; // Telegram topic routing is retired; every message targets the private chat root.
        string safeText = ChannelSemanticRendering.EscapeTelegramMarkdown(text, preserveMarkdown: true);
        string markdown = string.IsNullOrWhiteSpace(senderDisplayName)
            ? safeText
            : $"**{ChannelSemanticRendering.EscapeTelegramMarkdown(senderDisplayName)}**\n\n{safeText}";
        JsonObject body = new()
        {
            [chatIdProperty] = conversationId,
            ["reply_markup"] = ChannelSemanticRendering.CreateTelegramReplyKeyboardMarkup(),
            ["rich_message"] = new JsonObject
            {
                ["markdown"] = markdown,
                ["skip_entity_detection"] = true,
            },
        };
        return body;
    }

    private static JsonObject BuildInlineKeyboardPayload(string conversationId, string? conversationThreadId, string text, IReadOnlyList<ChannelInlineKeyboardRow> rows, string? parseMode = null, string? senderDisplayName = null)
    {
        _ = conversationThreadId; // Telegram topic routing is retired; every message targets the private chat root.
        (string resolvedText, string? resolvedParseMode) = ApplyTelegramAttribution(text, parseMode, senderDisplayName);

        JsonArray keyboardRows = new([.. rows.Select(row => (JsonNode?)new JsonArray([..
            row.Buttons.Select(button => (JsonNode?)new JsonObject
            {
                ["text"] = button.Label,
                ["callback_data"] = button.CallbackData,
            })]))]);

        JsonObject body = new()
        {
            [chatIdProperty] = conversationId,
            ["text"] = resolvedText,
            ["reply_markup"] = new JsonObject
            {
                ["inline_keyboard"] = keyboardRows,
            },
        };

        if (!string.IsNullOrWhiteSpace(resolvedParseMode))
        {
            body["parse_mode"] = resolvedParseMode;
        }

        return body;
    }

    private static JsonValue ParseTelegramNumberOrString(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? JsonValue.Create(parsed)!
            : JsonValue.Create(value)!;

    private static (string Text, string? ParseMode) ApplyTelegramAttribution(string text, string? parseMode, string? senderDisplayName)
    {
        if (string.IsNullOrWhiteSpace(senderDisplayName))
        {
            return (text, parseMode);
        }

        string safeName = EscapeHtml(senderDisplayName);
        string safeText = EscapeHtml(string.IsNullOrWhiteSpace(text) ? "(empty response)" : text);
        return ($"<b>{safeName}</b>\n{safeText}", "HTML");
    }

    private static string EscapeHtml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
