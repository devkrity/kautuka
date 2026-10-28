using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private static JsonObject CloneSettingsObject(JsonElement settings, string propertyName)
        => settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(property.GetRawText()) as JsonObject ?? new JsonObject()
                : new JsonObject();

    private static bool IsTopicMissingError(string? error)
        => !string.IsNullOrWhiteSpace(error)
            && (error.Contains("message thread not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("TOPIC_DELETED", StringComparison.OrdinalIgnoreCase)
                || error.Contains("TOPIC_ID_INVALID", StringComparison.OrdinalIgnoreCase));

    private async Task<OutboxExecutionResult> DeleteForumTopicAsync(
        PollingRegistration registration,
        string conversationId,
        string topicId,
        CancellationToken cancellationToken)
    {
        OutboxExecutionResult result = await SendTelegramRequestAsync(registration, "deleteForumTopic", new JsonObject
        {
            [chatIdProperty] = conversationId,
            ["message_thread_id"] = ParseTelegramNumberOrString(topicId),
        }, cancellationToken, treatUnrecoverableAsSuccess: false).ConfigureAwait(false);

        // Reconciliation is idempotent: the topic may already have been deleted by the user.
        return !result.Success && IsTopicMissingError(result.Error)
            ? new OutboxExecutionResult(true, null, null)
            : result;
    }
}
