using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Hitl;

internal static class ApprovalContinuationOrchestratorHelpers
{
    internal static string? TryReadCallerToolArguments(RunWaitState parentWaitState, string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(parentWaitState.SessionStateJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(parentWaitState.SessionStateJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("stateBag", out JsonElement stateBag)
                || !stateBag.TryGetProperty("InMemoryChatHistoryProvider", out JsonElement historyProvider)
                || !historyProvider.TryGetProperty("messages", out JsonElement messages)
                || messages.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("contents", out JsonElement contents)
                    || contents.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement content in contents.EnumerateArray())
                {
                    if (!content.TryGetProperty("$type", out JsonElement typeElement)
                        || !string.Equals(typeElement.GetString(), "functionCall", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!content.TryGetProperty("callId", out JsonElement callIdElement)
                        || !string.Equals(callIdElement.GetString(), toolCallId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return content.TryGetProperty("arguments", out JsonElement arguments)
                        ? arguments.GetRawText()
                        : null;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
