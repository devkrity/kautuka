using System.Text.Json;
using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class AgentExecutionNaradToolBodies
{
    public static JsonElement ToolCallStarted(GatewayChatToolCall toolCall)
    {
        JsonElement args = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
            ? JsonSerializer.SerializeToElement(new JsonObject(), KautukaJsonContext.Default.JsonObject)
            : JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;
        string executor = string.Equals(toolCall.ActionKind, "client", StringComparison.OrdinalIgnoreCase)
            ? "client"
            : "backend";
        return NaradEventBodies.ToolCallStarted(toolCall.ToolCallId, toolCall.ToolName, args, executor);
    }

    public static JsonElement ToolCallFinished(
        string toolCallId,
        string outcome,
        string? resultJson = null,
        string? toolName = null,
        string? code = null,
        string? message = null)
    {
        _ = toolName;
        JsonElement? response = null;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                response = JsonDocument.Parse(resultJson).RootElement;
            }
            catch (JsonException)
            {
                // A view tool journals the content text. That text is the result, and it is
                // often a filename or a document, not a JSON value.
                response = JsonDocument.Parse(JsonValue.Create(resultJson)!.ToJsonString()).RootElement;
            }
        }

        return NaradEventBodies.ToolCallFinished(toolCallId, outcome, response, code, message);
    }
}
