using System.Text.Json;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{
    private static async ValueTask<bool> HasExistingSessionHistoryAsync(
        AgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        JsonElement state = JsonDocument.Parse(
            await SerializeSessionStateJsonAsync(runtime, cancellationToken).ConfigureAwait(false)).RootElement;
        return TryGetSessionHistoryMessages(state, out JsonElement messages)
            && messages.GetArrayLength() > 0;
    }

    private static async ValueTask<bool> HasExistingToolCallInSessionHistoryAsync(
        AgentRuntime runtime,
        string toolCallId,
        CancellationToken cancellationToken)
    {
        JsonElement state = JsonDocument.Parse(
            await SerializeSessionStateJsonAsync(runtime, cancellationToken).ConfigureAwait(false)).RootElement;
        return SessionHistoryContainsToolCallId(state, toolCallId);
    }

    private static async ValueTask<string> SerializeSessionStateJsonAsync(
        AgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        MafSessionJson.StripAgentRequestMessageAttributions(runtime.Agent, runtime.Session);
        JsonElement state = await runtime.Agent.SerializeSessionAsync(
            runtime.Session,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return state.GetRawText();
    }

    internal static bool SessionHistoryContainsToolCallId(JsonElement state, string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)
            || !TryGetSessionHistoryMessages(state, out JsonElement messages))
        {
            return false;
        }

        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (JsonElementContainsStringValue(message, toolCallId))
                return true;
        }

        return false;
    }

    private static bool TryGetSessionHistoryMessages(JsonElement state, out JsonElement messages)
    {
        if (state.TryGetProperty("stateBag", out JsonElement stateBag)
            && stateBag.TryGetProperty("InMemoryChatHistoryProvider", out JsonElement historyProvider)
            && historyProvider.TryGetProperty("messages", out messages)
            && messages.ValueKind is JsonValueKind.Array)
        {
            return true;
        }

        messages = default;
        return false;
    }

    private static bool JsonElementContainsStringValue(JsonElement element, string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (JsonElementContainsStringValue(property.Value, value))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (JsonElementContainsStringValue(item, value))
                        return true;
                }

                return false;

            case JsonValueKind.String:
                return string.Equals(element.GetString(), value, StringComparison.Ordinal);

            default:
                return false;
        }
    }
}
