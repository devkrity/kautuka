using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.ModelCapability;

internal sealed partial class ModelProbeService
{
    private static GatewayModelCapabilities CreateUnknownCapabilities(string? probeError = null)
        => new(null, null, null, null, null, probeError, Timestamp());

    internal static GatewayModelCapabilities ClearProbeErrorWhenResolved(GatewayModelCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities.ProbeError))
        {
            return capabilities with { ProbeError = null };
        }

        bool hasDecisiveCoreCapabilities = capabilities.ToolCalling.HasValue
            && capabilities.Streaming.HasValue
            && capabilities.StructuredJson.HasValue
            && capabilities.AgentConversations.HasValue;

        return hasDecisiveCoreCapabilities
            ? capabilities with { ProbeError = null }
            : capabilities;
    }

    private static bool? Vote(int successes, int failures)
    {
        if (successes >= 2)
        {
            return true;
        }

        if (failures >= 2)
        {
            return false;
        }

        return null;
    }

    private static string SummarizeProbeFailure(Exception exception)
        => exception switch
        {
            System.ClientModel.ClientResultException clientResult =>
                $"{(int)clientResult.Status} {clientResult.Message}".Trim(),
            HttpRequestException httpRequest when !string.IsNullOrWhiteSpace(httpRequest.Message) => httpRequest.Message.Trim(),
            InvalidOperationException invalidOperation when !string.IsNullOrWhiteSpace(invalidOperation.Message) => invalidOperation.Message.Trim(),
            JsonException jsonException when !string.IsNullOrWhiteSpace(jsonException.Message) => jsonException.Message.Trim(),
            IOException ioException when !string.IsNullOrWhiteSpace(ioException.Message) => ioException.Message.Trim(),
            ArgumentOutOfRangeException outOfRange when !string.IsNullOrWhiteSpace(outOfRange.Message) => outOfRange.Message.Trim(),
            _ => exception.GetType().Name,
        };

    private static bool TryParseJson(string text, out JsonDocument? document)
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

    internal static (string ResponseText, string ThinkingText) ExtractThinking(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, string.Empty);

        var thinking = new System.Text.StringBuilder();
        string result = Regex.Replace(
            text,
            @"<(think|thought)>(.*?)</\1>",
            match =>
            {
                if (thinking.Length > 0) thinking.Append('\n');
                thinking.Append(match.Groups[2].Value.Trim());
                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return (result.Trim(), thinking.ToString());
    }

    internal static string ExtractThinkingFromMessages(IEnumerable<ChatMessage> messages)
    {
        var builder = new System.Text.StringBuilder();
        foreach (TextReasoningContent reasoning in messages
            .SelectMany(static m => m.Contents)
            .OfType<TextReasoningContent>())
        {
            if (string.IsNullOrWhiteSpace(reasoning.Text)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(reasoning.Text);
        }
        return builder.ToString();
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}

internal static partial class ModelProbeServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Streaming probe failed")]
    public static partial void StreamingProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Tool-call probe failed")]
    public static partial void ToolCallProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Structured-JSON probe raw: {Text}")]
    public static partial void StructuredJsonProbeRaw(ILogger logger, string text);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Structured-JSON probe failed")]
    public static partial void StructuredJsonProbeFailed(ILogger logger, Exception exception);
}
