using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Assets;
using Kautuka.Gateway.Domain.AgentFiles;

namespace Kautuka.Gateway.Domain.Assets;

/// <summary>
/// Reads one saved view file through the same document ingestion used for an uploaded file.
/// The tool text is that result. The view file stays the file the owner opens.
/// </summary>
internal static class ViewAttachmentReader
{
    public static async Task<string> ApplyAsync(
        string toolName,
        string resultJson,
        string? hostDirectory,
        string sessionId,
        IConversationAssetIngestionService ingestion,
        bool documentParsingEnabled,
        CancellationToken cancellationToken,
        MailImageHandoff? images = null,
        string? toolCallId = null)
    {
        if (!IsMailAttachmentRead(toolName)
            || string.IsNullOrWhiteSpace(resultJson)
            || string.IsNullOrWhiteSpace(hostDirectory))
        {
            return resultJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(resultJson);
        }
        catch (JsonException)
        {
            return resultJson;
        }

        if (root is not JsonObject payload
            || payload["structured_content"]?["view"]?["blocks"] is not JsonArray blocks)
        {
            return resultJson;
        }

        string? relativePath = null;
        string name = "attachment";
        string mediaType = "application/octet-stream";
        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block
                || !string.Equals(ReadString(block, "type"), "file", StringComparison.Ordinal)
                || string.Equals(ReadString(block, "role"), "source", StringComparison.Ordinal))
            {
                continue;
            }

            string? path = ReadString(block, "path");
            if (string.IsNullOrEmpty(path) || !path.StartsWith("view-files/", StringComparison.Ordinal))
            {
                continue;
            }

            relativePath = path;
            name = ReadString(block, "name") ?? name;
            mediaType = ReadString(block, "mediaType") ?? mediaType;
            break;
        }

        if (relativePath is null || !TryResolve(hostDirectory, relativePath, out string fullPath))
        {
            return resultJson;
        }

        string body = await ReadBodyAsync(
            fullPath,
            relativePath,
            name,
            mediaType,
            string.IsNullOrWhiteSpace(sessionId) ? "mail" : sessionId,
            ingestion,
            documentParsingEnabled,
            images,
            toolCallId,
            cancellationToken).ConfigureAwait(false);
        SetText(payload, string.IsNullOrEmpty(body) ? $"[{name}]" : $"[{name}]\n{body}");
        return payload.ToJsonString();
    }

    private static bool IsMailAttachmentRead(string toolName)
        => toolName is "mail_read_attachment" or "gmail_read_attachment";

    private static bool TryResolve(string hostDirectory, string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (!WorkspaceFile.TryResolveUnderRoot(hostDirectory, relative, out fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        return true;
    }

    private static async Task<string> ReadBodyAsync(
        string fullPath,
        string relativePath,
        string name,
        string mediaType,
        string sessionId,
        IConversationAssetIngestionService ingestion,
        bool documentParsingEnabled,
        MailImageHandoff? images,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        switch (ingestion.ClassifyAttachment(name, mediaType))
        {
            case AttachmentCategory.DirectText:
                using (FileStream text = WorkspaceFile.OpenRead(fullPath))
                {
                    using var reader = new StreamReader(text);
                    return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }

            case AttachmentCategory.ParseRequiredDocument:
                if (!documentParsingEnabled)
                {
                    return "Document parsing is turned off. The owner can open the file.";
                }

                using (FileStream document = WorkspaceFile.OpenRead(fullPath))
                {
                    try
                    {
                        ParsedAsset parsed = await ingestion.IngestParseRequiredAsync(
                            sessionId,
                            "mail-attachment-" + relativePath.Replace('/', '_'),
                            name,
                            document,
                            cancellationToken).ConfigureAwait(false);
                        if (parsed.IsSuccessful && parsed.NormalizedMarkdown is not null)
                        {
                            return parsed.NormalizedMarkdown;
                        }

                        return string.IsNullOrWhiteSpace(parsed.SummaryMessage)
                            ? "The document could not be read. The owner can open the file."
                            : parsed.SummaryMessage;
                    }
                    catch (IOException ex)
                    {
                        return ex.Message;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex.Message;
                    }
                }

            case AttachmentCategory.DirectImage:
                byte[] bytes;
                using (FileStream image = WorkspaceFile.OpenRead(fullPath))
                {
                    using var buffer = new MemoryStream();
                    await image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    bytes = buffer.ToArray();
                }
                if (images is not null && !string.IsNullOrEmpty(toolCallId) && bytes.Length > 0)
                {
                    images.Hold(toolCallId, name, mediaType, bytes);
                    return string.Empty;
                }

                long size = bytes.LongLength;
                return $"Image {mediaType}, {size.ToString(CultureInfo.InvariantCulture)} bytes. The owner can open it.";

            default:
                return "This file is not text or a document. The owner can open it.";
        }
    }

    private static string? ReadString(JsonObject block, string name)
        => block[name] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static void SetText(JsonObject payload, string text)
    {
        if (payload["content"] is not JsonArray content)
        {
            payload["content"] = new JsonArray
            {
                (JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            };
            return;
        }

        foreach (JsonNode? node in content)
        {
            if (node is JsonObject item && item.ContainsKey("text"))
            {
                item["text"] = text;
                return;
            }
        }

        content.Insert(0, new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        });
    }
}
