using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed record NaradUserMessage(string MessageId, string Content);

internal static class NaradRequestParsing
{
    private static readonly Regex workspaceReferencePattern = new(
        @"(^|[\s(\[\{\n])#([^#\r\n]+)",
        RegexOptions.Compiled);

    internal const int MaxMessageLength = 64 * 1024;
    internal const int MaxBodyLength = 256 * 1024;

    public static NaradUserMessage? TryExtractUserMessage(JsonElement body)
    {
        if (body.TryGetProperty("message", out JsonElement directMessage)
            && directMessage.ValueKind == JsonValueKind.String)
        {
            string content = directMessage.GetString()?.Trim() ?? string.Empty;
            if (content.Length > MaxMessageLength)
            {
                throw new NaradRequestValidationException("Message exceeds maximum length.");
            }

            string messageId = body.TryGetStringProperty("messageId") ?? Guid.NewGuid().ToString("N");
            return new NaradUserMessage(messageId, content);
        }

        if (!body.TryGetProperty("messages", out JsonElement messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement msg in messages.EnumerateArray().Reverse())
        {
            if (!msg.TryGetProperty("role", out JsonElement role)
                || !string.Equals(role.GetString(), "user", StringComparison.Ordinal)
                || !msg.TryGetProperty("content", out JsonElement content))
            {
                continue;
            }

            string text = ExtractUserMessageContent(content);
            if (string.IsNullOrWhiteSpace(text) && !HasAttachmentContent(content))
            {
                return null;
            }

            if (text.Length > MaxMessageLength)
            {
                throw new NaradRequestValidationException("Message exceeds maximum length.");
            }

            string messageId = msg.TryGetStringProperty("id") ?? Guid.NewGuid().ToString("N");
            return new NaradUserMessage(messageId, text);
        }

        return null;
    }

    public static long? ReadExpectedRevision(JsonElement body)
    {
        if (body.TryGetProperty("expectedRevision", out JsonElement revision)
            && revision.TryGetInt64(out long parsed))
        {
            return parsed;
        }

        return null;
    }

    public static IReadOnlyList<string> ExtractAssetIds(JsonElement body)
    {
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        if (body.TryGetProperty("assetIds", out JsonElement assetIdsElement) && assetIdsElement.ValueKind == JsonValueKind.Array)
        {
            AddAssetIds(assetIds, assetIdsElement);
        }

        if (body.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement msg in messages.EnumerateArray().Reverse())
            {
                if (!msg.TryGetProperty("role", out JsonElement role)
                    || !string.Equals(role.GetString(), "user", StringComparison.Ordinal))
                {
                    continue;
                }

                if (msg.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object
                            && part.TryGetProperty("metadata", out JsonElement partMetadata)
                            && partMetadata.ValueKind == JsonValueKind.Object)
                        {
                            AddAssetId(assetIds, partMetadata.TryGetStringProperty("assetId"));
                        }
                    }
                }

                break;
            }
        }

        if (body.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            AddAssetIds(assetIds, assets);
        }

        return [.. assetIds];
    }

    public static IReadOnlyList<string> ExtractWorkspaceFilePaths(JsonElement body)
    {
        List<string> paths = [];
        if (body.TryGetProperty("workspaceFiles", out JsonElement workspaceFiles) && workspaceFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in workspaceFiles.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    AddWorkspacePath(paths, entry.GetString());
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("path", out JsonElement pathElement)
                    && pathElement.ValueKind == JsonValueKind.String)
                {
                    AddWorkspacePath(paths, pathElement.GetString());
                }
            }
        }

        NaradUserMessage? message = TryExtractUserMessage(body);
        if (message is not null)
        {
            foreach (Match match in workspaceReferencePattern.Matches(message.Content))
            {
                AddWorkspacePath(paths, match.Groups[2].Value);
            }
        }

        return paths;
    }

    private static string ExtractUserMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out JsonElement typeElement)
                || !string.Equals(typeElement.GetString(), "text", StringComparison.Ordinal)
                || !part.TryGetProperty("text", out JsonElement textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString().Trim();
    }

    private static bool HasAttachmentContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("metadata", out JsonElement metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && !string.IsNullOrWhiteSpace(metadata.TryGetStringProperty("assetId")))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddWorkspacePath(List<string> paths, string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\\', '/').TrimStart('/').Trim();
        if (normalized.Length > 0 && !paths.Contains(normalized, StringComparer.Ordinal))
        {
            paths.Add(normalized);
        }
    }

    private static void AddAssetIds(HashSet<string> assetIds, JsonElement assets)
    {
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.String)
            {
                AddAssetId(assetIds, asset.GetString());
                continue;
            }

            if (asset.ValueKind == JsonValueKind.Object
                && asset.TryGetProperty("assetId", out JsonElement assetIdElement)
                && assetIdElement.ValueKind == JsonValueKind.String)
            {
                AddAssetId(assetIds, assetIdElement.GetString());
            }
        }
    }

    private static void AddAssetId(HashSet<string> assetIds, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            assetIds.Add(value);
        }
    }

    public static IEnumerable<string> GetWorkspaceFileCandidateVariants(string path)
    {
        string current = path.Trim().TrimEnd(')', ',', '.', ';', ':', '!', '?').TrimEnd();
        while (current.Length > 0)
        {
            yield return current;

            int lastSpaceIndex = current.LastIndexOf(' ');
            if (lastSpaceIndex < 0)
            {
                yield break;
            }

            current = current[..lastSpaceIndex].TrimEnd(')', ',', '.', ';', ':', '!', '?').TrimEnd();
        }
    }

    internal static string? TryGetStringProperty(this JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed class NaradRequestValidationException : Exception
{
    public NaradRequestValidationException()
    {
    }

    public NaradRequestValidationException(string message)
        : base(message)
    {
    }

    public NaradRequestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
