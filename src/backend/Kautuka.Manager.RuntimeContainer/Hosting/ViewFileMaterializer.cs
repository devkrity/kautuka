using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Moves view file bytes onto the host workspace. The connector process never receives that directory.
/// </summary>
public static class ViewFileMaterializer
{
    public static string Materialize(string resultJson, string? hostDirectory, ViewFileOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return resultJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(resultJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return resultJson;
        }

        if (root is not JsonObject payload
            || payload["structured_content"] is not JsonObject structured
            || structured["files"] is not JsonArray files)
        {
            return resultJson;
        }

        JsonArray? blocks = structured["view"]?["blocks"] as JsonArray;
        options ??= new ViewFileOptions();
        int fileCount = 0;
        foreach (JsonNode? node in files)
        {
            if (node is not JsonObject file)
            {
                continue;
            }

            string fileId = file["fileId"]?.GetValue<string>() ?? "file";
            string name = SafeFileName(file["name"]?.GetValue<string>() ?? "file");
            string? base64 = file["base64"]?.GetValue<string>();
            file.Remove("base64");
            JsonObject? block = FindBlock(blocks, fileId);
            if (++fileCount > options.MaxFilesPerResult || string.IsNullOrWhiteSpace(base64) || string.IsNullOrWhiteSpace(hostDirectory))
            {
                if (block is not null)
                {
                    block.Remove("path");
                    block["unavailable"] = true;
                    block["unavailableReason"] = "This file could not be saved safely. Check the workspace and file limits, then try again.";
                }

                continue;
            }

            string folder = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileId)))[..12];
            try
            {
                long encodedLength = base64.Count(static ch => ch is not (' ' or '\t' or '\n' or '\r'));
                if (options.MaxFileBytes < 0 || encodedLength > ((long)options.MaxFileBytes + 2) / 3 * 4)
                    throw new InvalidDataException("View file exceeds the configured size limit.");
                byte[] bytes = Convert.FromBase64String(base64);
                if (bytes.Length > options.MaxFileBytes || name.Contains('\0', StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid view file size or name.");
                if (OperatingSystem.IsWindows())
                    WindowsViewFiles.Write(hostDirectory, folder, name, bytes);
                else if (DescriptorFiles.IsSupportedPlatform())
                    DescriptorFiles.WriteViewFile(hostDirectory, folder, name, bytes);
                else
                    throw new IOException("Secure file saving is unavailable on this device.");
                if (block is not null)
                    block["path"] = $"view-files/{folder}/{name}";
            }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                if (block is not null)
                {
                    block.Remove("path");
                    block["unavailable"] = true;
                    block["unavailableReason"] = "This file could not be saved safely. Check the workspace and file limits, then try again.";
                }
            }
        }

        structured.Remove("files");
        return payload.ToJsonString();
    }

    private static JsonObject? FindBlock(JsonArray? blocks, string fileId)
    {
        if (blocks is null)
        {
            return null;
        }

        foreach (JsonNode? node in blocks)
        {
            if (node is JsonObject block
                && string.Equals(block["type"]?.GetValue<string>(), "file", StringComparison.Ordinal)
                && string.Equals(block["fileId"]?.GetValue<string>(), fileId, StringComparison.Ordinal))
            {
                return block;
            }
        }

        return null;
    }

    private static string SafeFileName(string name)
    {
        string leaf = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(leaf) || leaf is "." or ".." ? "file" : leaf;
    }
}

public sealed class ViewFileOptions
{
    public long MaxFileBytes { get; set; } = 25L * 1024 * 1024;
    public int MaxFilesPerResult { get; set; } = 20;
}
