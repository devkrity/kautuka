using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class McpToolContractCodec
{
    public static Dictionary<string, object?> ParseInput(string toolInputJson)
    {
        if (string.IsNullOrWhiteSpace(toolInputJson))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(toolInputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? ToDictionary(document.RootElement) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeResult(CallToolResult result)
    {
        var payload = new JsonObject();
        if (result.IsError is bool isError)
        {
            payload["is_error"] = isError;
        }

        if (result.StructuredContent is JsonElement structuredContent)
        {
            payload["structured_content"] = JsonNode.Parse(structuredContent.GetRawText());
        }

        if (result.Content.Count > 0)
        {
            var content = new JsonArray();
            foreach (ContentBlock block in result.Content)
            {
                content.Add((JsonNode?)ToContent(block));
            }

            payload["content"] = content;
        }

        if (result.Meta is not null)
        {
            payload["meta"] = result.Meta.DeepClone();
        }

        return payload.ToJsonString();
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Object or JsonValueKind.Array => property.Value.Clone(),
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out long integer) ? integer : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        return dictionary;
    }

    private static JsonObject ToContent(ContentBlock block)
    {
        JsonObject payload = new() { ["type"] = block.Type };
        switch (block)
        {
            case TextContentBlock text:
                payload["text"] = text.Text;
                break;
#pragma warning disable MCP9005 // ToolResultContentBlock remains on the wire for down-level peers.
            case ToolResultContentBlock toolResult:
                payload["tool_use_id"] = toolResult.ToolUseId;
                payload["input"] = JsonNode.Parse(toolResult.StructuredContent?.GetRawText() ?? "{}");
                break;
#pragma warning restore MCP9005
            case ResourceLinkBlock resourceLink:
                payload["uri"] = resourceLink.Uri;
                payload["name"] = resourceLink.Name;
                payload["title"] = resourceLink.Title;
                payload["description"] = resourceLink.Description;
                payload["mime_type"] = resourceLink.MimeType;
                if (resourceLink.Size is long size)
                {
                    payload["size"] = size;
                }

                break;
            case EmbeddedResourceBlock embedded:
                payload["resource"] = embedded.Resource?.ToString();
                break;
            case ImageContentBlock image:
                payload["mime_type"] = image.MimeType;
                payload["data_base64"] = Convert.ToBase64String(image.DecodedData.ToArray());
                break;
            case AudioContentBlock audio:
                payload["mime_type"] = audio.MimeType;
                payload["data_base64"] = Convert.ToBase64String(audio.DecodedData.ToArray());
                break;
        }

        return payload;
    }
}
