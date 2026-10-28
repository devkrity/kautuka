using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ChatToolView
{
    internal static JsonObject? Take(object? rawResult, ref string? resultJson)
    {
        JsonObject? root = rawResult switch
        {
            JsonObject node => node,
            JsonNode node => node as JsonObject,
            JsonElement element when element.ValueKind == JsonValueKind.Object => JsonNode.Parse(element.GetRawText()) as JsonObject,
            _ => null,
        };
        if (root?["structured_content"] is not JsonObject structured || structured["view"] is not JsonObject view)
        {
            return null;
        }

        if (root["content"] is JsonArray content)
        {
            foreach (JsonNode? block in content)
            {
                if (block is JsonObject item && item["text"]?.GetValue<string>() is string text && !string.IsNullOrWhiteSpace(text))
                {
                    resultJson = text;
                    break;
                }
            }
        }

        return view;
    }
}
