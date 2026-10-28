using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI.ProviderPathPolicy;

internal sealed partial class ProviderPathTelemetryChatClient
{
    private static ChatResponse PromoteReasoningToText(ChatResponse response)
    {
        var promoted = new List<ChatMessage>();
        foreach (ChatMessage msg in response.Messages)
        {
            var newContents = new List<AIContent>();
            foreach (AIContent content in msg.Contents)
            {
                if (content is TextReasoningContent rc && !string.IsNullOrWhiteSpace(rc.Text))
                    newContents.Add(new TextContent(rc.Text));
                else
                    newContents.Add(content);
            }

            promoted.Add(new ChatMessage(msg.Role, newContents));
        }

        return new ChatResponse(promoted)
        {
            FinishReason = response.FinishReason,
            ModelId = response.ModelId,
            Usage = response.Usage,
        };
    }

    private static readonly Regex thinkingTagRegex = new(
        @"<(think|thinking|thought)[\s\S]*?</(think|thinking|thought)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsAllTaggedReasoning(string text)
    {
        string remainder = thinkingTagRegex.Replace(text, string.Empty);
        return string.IsNullOrWhiteSpace(remainder);
    }

    internal static bool TryParseJson(string text, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string json = text;
        Match fence = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fence.Success)
            json = fence.Groups[1].Value.Trim();

        if (json.Length == 0 || json[0] == '<') return false;

        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static partial class ProviderPathTelemetryChatClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson mode active for {Key}, running tool dispatch loop")]
    public static partial void PromptedJsonModeActive(ILogger logger, string key);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson mode active for {Key} (streaming), running tool dispatch loop")]
    public static partial void PromptedJsonStreamingModeActive(ILogger logger, string key);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: promoting reasoning-only output ({Len} chars) to text on path {Key}")]
    public static partial void PromotingReasoningOnlyOutput(ILogger logger, int len, string key);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson turn {Turn}: no tool call found in text ({TextLen}) or reasoning ({ReasoningLen}) for {Key}")]
    public static partial void PromptedJsonNoToolCallFound(ILogger logger, int turn, int textLen, int reasoningLen, string key);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson turn {Turn}: empty response, injecting summary prompt for {Key}")]
    public static partial void PromptedJsonInjectingSummaryPrompt(ILogger logger, int turn, string key);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson turn {Turn}: tool call '{Tool}' found in reasoning block for {Key}")]
    public static partial void PromptedJsonToolCallFoundInReasoning(ILogger logger, int turn, string tool, string key);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "ProviderPathTelemetry: PromptedJson got unknown tool '{Tool}' for {Key}")]
    public static partial void PromptedJsonUnknownTool(ILogger logger, string tool, string key);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "ProviderPathTelemetry: PromptedJson dispatching tool '{Tool}' turn {Turn} for {Key}")]
    public static partial void PromptedJsonDispatchingTool(ILogger logger, string tool, int turn, string key);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "ProviderPathTelemetry: PromptedJson tool '{Tool}' invocation failed")]
    public static partial void PromptedJsonToolInvocationFailed(ILogger logger, Exception exception, string tool);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "ProviderPathTelemetry: PromptedJson exceeded max turns ({Max}) for {Key}")]
    public static partial void PromptedJsonExceededMaxTurns(ILogger logger, int max, string key);
}
