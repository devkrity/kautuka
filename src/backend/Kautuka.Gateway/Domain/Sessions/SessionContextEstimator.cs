namespace Kautuka.Gateway.Domain.Sessions;

internal static class SessionContextEstimator
{
    public const int CompactedRecentTailMessageCount = 8;

    private const int fixedPromptOverheadTokens = 512;
    private const int perMessageOverheadTokens = 24;
    private const int perToolCallOverheadTokens = 32;
    private const int perAssetOverheadTokens = 12;

    public static int EstimateTokens(IReadOnlyList<GatewayMessage> messages, int injectionTokenBudget)
    {
        int total = fixedPromptOverheadTokens + Math.Max(0, injectionTokenBudget);

        foreach (GatewayMessage message in messages)
        {
            total += perMessageOverheadTokens;
            total += EstimateTextTokens(message.Content);
            total += EstimateTextTokens(message.Thinking);

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (GatewayToolCall toolCall in message.ToolCalls)
                {
                    total += perToolCallOverheadTokens;
                    total += EstimateTextTokens(toolCall.ToolName);
                    total += EstimateTextTokens(toolCall.ArgumentsJson);
                    total += EstimateTextTokens(toolCall.ResultJson);
                }
            }

            if (message.Assets is { Count: > 0 })
            {
                total += message.Assets.Count * perAssetOverheadTokens;
                foreach (GatewayConversationAsset asset in message.Assets)
                {
                    total += EstimateTextTokens(asset.FileName);
                    total += EstimateTextTokens(asset.ContentType);
                }
            }
        }

        return total;
    }

    public static int EstimateUserMessageTokens(string? messageContent)
        => perMessageOverheadTokens + EstimateTextTokens(messageContent);

    public static int EstimateCompactedTokens(
        string? compactSummary,
        IReadOnlyList<GatewayMessage> messages,
        int injectionTokenBudget,
        int recentTailMessageCount = CompactedRecentTailMessageCount)
    {
        int total = fixedPromptOverheadTokens + Math.Max(0, injectionTokenBudget);
        total += perMessageOverheadTokens;
        total += EstimateTextTokens(compactSummary);

        IReadOnlyList<GatewayMessage> recentTail = messages.Count <= recentTailMessageCount
            ? messages
            : messages.Skip(Math.Max(0, messages.Count - recentTailMessageCount)).ToArray();

        foreach (GatewayMessage message in recentTail)
        {
            total += perMessageOverheadTokens;
            total += EstimateTextTokens(message.Content);
            total += EstimateTextTokens(message.Thinking);

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (GatewayToolCall toolCall in message.ToolCalls)
                {
                    total += perToolCallOverheadTokens;
                    total += EstimateTextTokens(toolCall.ToolName);
                    total += EstimateTextTokens(toolCall.ArgumentsJson);
                    total += EstimateTextTokens(toolCall.ResultJson);
                }
            }

            if (message.Assets is { Count: > 0 })
            {
                total += message.Assets.Count * perAssetOverheadTokens;
                foreach (GatewayConversationAsset asset in message.Assets)
                {
                    total += EstimateTextTokens(asset.FileName);
                    total += EstimateTextTokens(asset.ContentType);
                }
            }
        }

        return total;
    }

    public static int EstimateTextTokens(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : Math.Max(1, (value.Length + 3) / 4);
}
