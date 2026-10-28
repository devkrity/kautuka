using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ChatToolResultProcessor
{
    private const long maxToolArtifactBytes = 15L * 1024L * 1024L;
    internal sealed record PendingConversationAsset(
        string FileName,
        string ContentType,
        string Disposition,
        byte[] ContentBytes);

    internal sealed record ProcessedToolResult(
        string? ResultJson,
        IReadOnlyList<PendingConversationAsset> Assets,
        JsonObject? View = null);

    internal static string? SerializeToolResult(object? rawResult, string? toolName = null)
    {
        return rawResult switch
        {
            null => null,
            string text => text,
            JsonNode node => node.ToJsonString(),
            JsonElement element => ExtractToolResultText(toolName, element),
            TextContent textContent => textContent.Text,
            TextReasoningContent reasoningContent => reasoningContent.Text,
            DataContent => "[binary tool result]",
            IEnumerable<AIContent> contents => SerializeAiContents(contents),
            _ => SerializeUnknownToolResult(rawResult),
        };
    }

    [ThreadStatic]
    private static string? allowedReadRoot;

    internal static ProcessedToolResult ProcessToolResult(object? rawResult, string? toolName = null, string? allowedRoot = null)
    {
        string? previousRoot = allowedReadRoot;
        allowedReadRoot = allowedRoot;
        try
        {
            return ProcessToolResultCore(rawResult, toolName);
        }
        finally
        {
            allowedReadRoot = previousRoot;
        }
    }

    private static ProcessedToolResult ProcessToolResultCore(object? rawResult, string? toolName)
    {
        var assets = new List<PendingConversationAsset>();

        string? resultJson = rawResult switch
        {
            null => null,
            string text => text,
            DataContent dataContent => SerializeDataContent(dataContent, assets),
            IEnumerable<AIContent> contents => SerializeAiContents(contents, assets),
            JsonElement element => SerializeJsonWithAssets(element, toolName, assets),
            JsonNode node => SerializeJsonNodeWithAssets(node, assets),
            _ => SerializeUnknownToolResultWithAssets(rawResult, assets),
        };

        if (assets.Count > 0 && string.IsNullOrWhiteSpace(resultJson))
        {
            resultJson = BuildAssetSummary(assets).ToJsonString();
        }

        JsonObject? view = ChatToolView.Take(rawResult, ref resultJson);
        return new ProcessedToolResult(resultJson, assets, view);
    }

    private static string SerializeUnknownToolResult(object rawResult)
    {
        JsonNode? node = ToJsonNode(rawResult, depth: 0);
        if (node is not null)
        {
            return node.ToJsonString();
        }

        return rawResult.ToString() ?? rawResult.GetType().FullName ?? rawResult.GetType().Name;
    }

    private static string SerializeUnknownToolResultWithAssets(object rawResult, List<PendingConversationAsset> assets)
    {
        JsonNode? node = ToJsonNode(rawResult, depth: 0);
        if (node is not null)
        {
            return SerializeJsonNodeWithAssets(node, assets) ?? node.ToJsonString();
        }

        return SerializeToolResult(rawResult) ?? rawResult.ToString() ?? rawResult.GetType().FullName ?? rawResult.GetType().Name;
    }

    private static JsonNode? ToJsonNode(object? value, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > 8)
        {
            return JsonValue.Create("[max-depth]");
        }

        return value switch
        {
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => JsonValue.Create(text),
            char character => JsonValue.Create(character.ToString()),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            DateTime timestamp => JsonValue.Create(timestamp),
            DateTimeOffset timestamp => JsonValue.Create(timestamp),
            DateOnly date => JsonValue.Create(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            TimeOnly time => JsonValue.Create(time.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            TimeSpan duration => JsonValue.Create(duration.ToString()),
            Guid guid => JsonValue.Create(guid),
            Uri uri => JsonValue.Create(uri.ToString()),
            Enum enumValue => JsonValue.Create(enumValue.ToString()),
            IDictionary dictionary => ToJsonObject(dictionary, depth + 1),
            IEnumerable sequence when value is not string => ToJsonArray(sequence, depth + 1),
            _ => null,
        };
    }

    private static JsonObject ToJsonObject(IDictionary dictionary, int depth)
    {
        var obj = new JsonObject();

        foreach (DictionaryEntry entry in dictionary)
        {
            string key = entry.Key?.ToString() ?? string.Empty;
            obj[key] = ToJsonNode(entry.Value, depth);
        }

        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable sequence, int depth)
    {
        var array = new JsonArray();

        foreach (object? item in sequence)
        {
            array.Add(ToJsonNode(item, depth));
        }

        return array;
    }

    private static string SerializeAiContents(IEnumerable<AIContent> contents)
        => SerializeAiContents(contents, assets: null);

    private static string SerializeAiContents(IEnumerable<AIContent> contents, List<PendingConversationAsset>? assets)
    {
        var parts = new List<string>();

        foreach (AIContent content in contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    parts.Add(textContent.Text);
                    break;
                case TextReasoningContent reasoningContent when !string.IsNullOrWhiteSpace(reasoningContent.Text):
                    parts.Add(reasoningContent.Text);
                    break;
                case DataContent dataContent:
                    if (assets is not null
                        && TryCreatePendingAsset(dataContent, out PendingConversationAsset? asset)
                        && asset is not null)
                    {
                        assets.Add(asset);
                        parts.Add($"[generated file: {asset.FileName}]");
                    }
                    else
                    {
                        parts.Add("[binary tool result]");
                    }
                    break;
            }
        }

        if (parts.Count > 0)
        {
            return string.Join("\n", parts);
        }

        return "[]";
    }

    private static string? SerializeDataContent(DataContent dataContent, List<PendingConversationAsset> assets)
    {
        if (TryCreatePendingAsset(dataContent, out PendingConversationAsset? asset) && asset is not null)
        {
            assets.Add(asset);
            return BuildAssetSummary([asset]).ToJsonString();
        }

        return "[binary tool result]";
    }

    private static string? SerializeJsonWithAssets(JsonElement element, string? toolName, List<PendingConversationAsset> assets)
    {
        JsonNode? parsed = JsonNode.Parse(element.GetRawText());
        if (parsed is null)
        {
            return ExtractToolResultText(toolName, element);
        }

        string? sanitized = SerializeJsonNodeWithAssets(parsed, assets);
        if (assets.Count == 0)
        {
            return ExtractToolResultText(toolName, element);
        }

        return sanitized;
    }

    private static string? SerializeJsonNodeWithAssets(JsonNode node, List<PendingConversationAsset> assets)
    {
        JsonNode? sanitized = SanitizeJsonNode(node, assets);
        return sanitized?.ToJsonString();
    }

    private static JsonNode? SanitizeJsonNode(JsonNode? node, List<PendingConversationAsset> assets)
    {
        return node switch
        {
            null => null,
            JsonObject obj => SanitizeJsonObject(obj, assets),
            JsonArray array => SanitizeJsonArray(array, assets),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject SanitizeJsonObject(JsonObject obj, List<PendingConversationAsset> assets)
    {
        if (TryCreatePendingAsset(obj, out PendingConversationAsset? asset, out JsonObject? summary)
            && asset is not null
            && summary is not null)
        {
            assets.Add(asset);
            return summary;
        }

        var clone = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (IsArtifactCollectionProperty(property.Key)
                && TrySanitizeArtifactCollection(property.Value, assets, out JsonNode? sanitizedCollection))
            {
                clone[property.Key] = sanitizedCollection;
                continue;
            }

            clone[property.Key] = SanitizeJsonNode(property.Value, assets);
        }

        return clone;
    }

    private static JsonArray SanitizeJsonArray(JsonArray array, List<PendingConversationAsset> assets)
    {
        var clone = new JsonArray();
        foreach (JsonNode? item in array)
        {
            clone.Add(SanitizeJsonNode(item, assets));
        }

        return clone;
    }

    private static bool TrySanitizeArtifactCollection(JsonNode? value, List<PendingConversationAsset> assets, out JsonNode? sanitized)
    {
        sanitized = null;

        if (value is JsonObject obj)
        {
            if (!TryCreatePendingAsset(obj, out PendingConversationAsset? asset, out JsonObject? summary)
                || asset is null
                || summary is null)
            {
                return false;
            }

            assets.Add(asset);
            sanitized = summary;
            return true;
        }

        if (value is not JsonArray array)
        {
            return false;
        }

        var summaries = new JsonArray();
        bool matched = false;
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject itemObj
                && TryCreatePendingAsset(itemObj, out PendingConversationAsset? asset, out JsonObject? summary)
                && asset is not null
                && summary is not null)
            {
                assets.Add(asset);
                summaries.Add((JsonNode?)summary);
                matched = true;
                continue;
            }

            summaries.Add(SanitizeJsonNode(item, assets));
        }

        if (!matched)
        {
            return false;
        }

        sanitized = summaries;
        return true;
    }

    private static bool IsArtifactCollectionProperty(string propertyName)
        => propertyName.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("assets", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("files", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreatePendingAsset(DataContent dataContent, out PendingConversationAsset? asset)
    {
        asset = null;

        if (!TryReadDataContentBytes(dataContent, out byte[]? bytes, out string? pathValue))
        {
            return false;
        }

        string fileName = !string.IsNullOrWhiteSpace(dataContent.Name)
            ? dataContent.Name
            : GetSafeFileNameFromPath(pathValue)
                ?? InferFileNameFromContentType(dataContent.MediaType)
                ?? "generated-file";
        string contentType = NormalizeContentType(dataContent.MediaType, fileName, fallbackText: false);
        if (bytes is null)
        {
            return false;
        }

        asset = new PendingConversationAsset(fileName, contentType, "download", bytes);
        return true;
    }

    private static bool TryCreatePendingAsset(JsonObject obj, out PendingConversationAsset? asset, out JsonObject? summary)
    {
        asset = null;
        summary = null;

        string? path = GetStringProperty(obj, "path")
            ?? GetStringProperty(obj, "filePath")
            ?? GetStringProperty(obj, "uri");
        string? fileName = GetStringProperty(obj, "fileName")
            ?? GetStringProperty(obj, "name");
        string? contentType = GetStringProperty(obj, "contentType")
            ?? GetStringProperty(obj, "mediaType")
            ?? GetStringProperty(obj, "mimeType");
        string disposition = NormalizeDisposition(GetStringProperty(obj, "disposition"));

        if (TryDecodeArtifactBytes(obj, fileName, contentType, path, out byte[]? contentBytes, out string? resolvedFileName, out string? resolvedContentType)
            && contentBytes is not null
            && resolvedFileName is not null
            && resolvedContentType is not null)
        {
            asset = new PendingConversationAsset(resolvedFileName, resolvedContentType, disposition, contentBytes);
            summary = BuildAssetSummaryNode(asset);
            return true;
        }

        return false;
    }

    private static bool TryDecodeArtifactBytes(
        JsonObject obj,
        string? fileName,
        string? contentType,
        string? path,
        out byte[]? contentBytes,
        out string? resolvedFileName,
        out string? resolvedContentType)
    {
        contentBytes = null;
        resolvedFileName = null;
        resolvedContentType = null;

        string? base64 = GetStringProperty(obj, "base64Data")
            ?? GetStringProperty(obj, "contentBase64")
            ?? GetStringProperty(obj, "base64");
        if (!string.IsNullOrWhiteSpace(base64))
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            try
            {
                contentBytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return false;
            }

            resolvedFileName = fileName;
            resolvedContentType = NormalizeContentType(contentType, fileName, fallbackText: false);
            return true;
        }

        string? text = GetStringProperty(obj, "text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            contentBytes = Encoding.UTF8.GetBytes(text);
            resolvedFileName = fileName;
            resolvedContentType = NormalizeContentType(contentType, fileName, fallbackText: true);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(path) && TryReadBytesFromPath(path, out byte[]? fileBytes, out string? pathFileName))
        {
            contentBytes = fileBytes;
            resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? pathFileName : fileName;
            resolvedContentType = NormalizeContentType(contentType, resolvedFileName, fallbackText: false);
            return true;
        }

        return false;
    }

    private static bool TryReadDataContentBytes(DataContent dataContent, out byte[]? bytes, out string? pathValue)
    {
        bytes = null;
        pathValue = null;

        if (!dataContent.Data.IsEmpty)
        {
            bytes = dataContent.Data.ToArray();
            return true;
        }

        if (!dataContent.Base64Data.IsEmpty)
        {
            try
            {
                bytes = Convert.FromBase64String(dataContent.Base64Data.ToString());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(dataContent.Uri) && TryReadBytesFromPath(dataContent.Uri, out byte[]? fileBytes, out string? fileName))
        {
            bytes = fileBytes;
            pathValue = fileName;
            return true;
        }

        return false;
    }

    private static bool TryReadBytesFromPath(string rawPath, out byte[]? bytes, out string? fileName)
    {
        bytes = null;
        fileName = null;

        if (!WorkspaceFile.TryReadUnderRoot(allowedReadRoot, rawPath, maxToolArtifactBytes, out bytes, out fileName) || bytes is null)
        {
            return false;
        }

        return true;
    }

    private static string NormalizeContentType(string? contentType, string? fileName, bool fallbackText)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return contentType;
        }

        string extension = Path.GetExtension(fileName ?? string.Empty).ToUpperInvariant();
        return extension switch
        {
            ".TXT" => "text/plain",
            ".MD" => "text/markdown",
            ".JSON" => "application/json",
            ".CSV" => "text/csv",
            ".HTML" => "text/html",
            ".PDF" => "application/pdf",
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".WEBP" => "image/webp",
            ".ZIP" => "application/zip",
            _ when fallbackText => "text/plain",
            _ => "application/octet-stream",
        };
    }

    private static string NormalizeDisposition(string? disposition)
        => string.Equals(disposition, "inlinePreview", StringComparison.OrdinalIgnoreCase)
            ? "inlinePreview"
            : "download";

    private static string? GetStringProperty(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static JsonObject BuildAssetSummaryNode(PendingConversationAsset asset)
        => new()
        {
            ["generatedAsset"] = true,
            ["fileName"] = asset.FileName,
            ["contentType"] = asset.ContentType,
            ["sizeBytes"] = asset.ContentBytes.Length,
            ["disposition"] = asset.Disposition,
        };

    private static JsonArray BuildAssetSummary(IReadOnlyList<PendingConversationAsset> assets)
    {
        var array = new JsonArray();
        foreach (PendingConversationAsset asset in assets)
        {
            array.Add((JsonNode?)BuildAssetSummaryNode(asset));
        }

        return array;
    }

    private static string? GetSafeFileNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFileName(path);
    }

    private static string? InferFileNameFromContentType(string? contentType)
        => contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image-output" : null;

    internal static string ExtractToolResultText(string? toolName, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (!TryExtractContentArrayText(element, out string? text))
        {
            return element.GetRawText();
        }

        if (!IsToolError(element))
        {
            return text!;
        }

        string name = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
        return $"Tool '{name}' returned an error: {text}";
    }

    private static bool TryExtractContentArrayText(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("content", out JsonElement contentArray)
            || contentArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var builder = new StringBuilder();
        foreach (JsonElement item in contentArray.EnumerateArray())
        {
            if (!item.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(textElement.GetString());
        }

        if (builder.Length == 0)
        {
            return false;
        }

        text = builder.ToString();
        return true;
    }

    private static bool IsToolError(JsonElement element)
        => element.TryGetProperty("isError", out JsonElement isErrorElement)
            && isErrorElement.ValueKind == JsonValueKind.True;

}
