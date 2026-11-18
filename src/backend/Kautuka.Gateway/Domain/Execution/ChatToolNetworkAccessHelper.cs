using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Resolves network-access failure details when an agent tool execution fails without
/// elevating privileges or exposing another user's pending destinations.
/// </summary>
internal static class ChatToolNetworkAccessHelper
{
    public static NetworkAccessDeniedException? FindNetworkAccessDeniedException(Exception ex)
    {
        return ex as NetworkAccessDeniedException
            ?? ex.InnerException as NetworkAccessDeniedException
            ?? (ex is AggregateException agg ? agg.InnerExceptions.OfType<NetworkAccessDeniedException>().FirstOrDefault() : null);
    }

    public static Task<JsonElement> BuildToolCallFinishedPayloadAsync(
        Exception ex,
        NetworkAccessDeniedException? nacEx,
        string activeToolCallId,
        string? activeToolName)
    {
        string code = nacEx?.NaradReason ?? "tool_execution_failed";
        string? destination = nacEx?.Destination;
        int? port = nacEx?.Port;
        string? requestId = nacEx?.RequestId;
        string? approvalUrl = nacEx is not null ? "/network-access" : null;

        var toolFinishedPayload = new JsonObject
        {
            ["toolCallId"] = activeToolCallId,
            ["toolName"] = activeToolName,
            ["response"] = null,
            ["outcome"] = "failed",
            ["code"] = code,
            ["message"] = ex.Message,
        };
        if (!string.IsNullOrWhiteSpace(destination))
        {
            toolFinishedPayload["destination"] = destination;
            toolFinishedPayload["reason"] = code;
        }
        if (port.HasValue)
        {
            toolFinishedPayload["port"] = port.Value;
        }
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            toolFinishedPayload["requestId"] = requestId;
        }
        if (!string.IsNullOrWhiteSpace(approvalUrl))
        {
            toolFinishedPayload["approvalUrl"] = approvalUrl;
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(toolFinishedPayload, KautukaJsonContext.Default.JsonObject));
    }
}
