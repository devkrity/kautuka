using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Moves view file bytes onto the host workspace. The connector process never receives that directory.
/// </summary>
public static class ViewFileMaterializer
{
    public static string Materialize(string resultJson, string? hostDirectory)
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
            if (string.IsNullOrWhiteSpace(base64) || string.IsNullOrWhiteSpace(hostDirectory))
            {
                if (block is not null)
                {
                    block["unavailable"] = true;
                }

                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                if (block is not null)
                {
                    block["unavailable"] = true;
                }

                continue;
            }

            string folder = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileId)))[..12];
            string relative = Path.Combine("view-files", folder, name);
            string fullPath = Path.Combine(hostDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            if (block is not null)
            {
                block["path"] = relative.Replace('\\', '/');
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
