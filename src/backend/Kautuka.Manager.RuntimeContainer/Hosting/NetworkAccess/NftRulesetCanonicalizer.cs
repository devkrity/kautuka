using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

internal static class NftRulesetCanonicalizer
{
    /// <summary>
    /// Computes a canonical SHA-256 hash from <c>nft -j list table ...</c> JSON output.
    /// Strips transient runtime state (<c>handle</c>, <c>metainfo</c>, and dynamic packet/byte counts),
    /// verifies that all required comment tags and the workload IP (when specified) are present,
    /// and hashes the canonicalized JSON tree.
    /// </summary>
    internal static string ComputeHash(
        string nftJsonOutput,
        IReadOnlyList<string>? requiredComments = null,
        string? requiredWorkloadIp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nftJsonOutput);
        JsonNode root = JsonNode.Parse(nftJsonOutput)
            ?? throw new InvalidOperationException("Failed to parse nft JSON output.");

        var foundComments = new HashSet<string>(StringComparer.Ordinal);
        bool foundWorkloadIp = false;

        JsonArray nftablesArray;
        if (root is JsonObject obj && obj["nftables"] is JsonArray arr)
        {
            nftablesArray = arr;
        }
        else if (root is JsonArray directArr)
        {
            nftablesArray = directArr;
        }
        else
        {
            throw new InvalidOperationException("Unexpected nft JSON root element.");
        }

        var canonicalArray = new JsonArray();
        foreach (JsonNode? item in nftablesArray)
        {
            if (item is null)
            {
                continue;
            }

            // Omit metainfo object entirely (timestamp, version, etc.)
            if (item is JsonObject itemObj && itemObj.ContainsKey("metainfo"))
            {
                continue;
            }

            JsonNode canonicalNode = CanonicalizeNode(item, foundComments, ref foundWorkloadIp, requiredWorkloadIp);
            canonicalArray.Add(canonicalNode);
        }

        // Verify required comments:
        if (requiredComments is not null)
        {
            foreach (string required in requiredComments)
            {
                if (!foundComments.Contains(required))
                {
                    throw new InvalidOperationException($"Canonical nft verification failed: missing required comment tag '{required}'.");
                }
            }
        }

        // Verify required workload IP:
        if (!string.IsNullOrWhiteSpace(requiredWorkloadIp) && !foundWorkloadIp)
        {
            throw new InvalidOperationException($"Canonical nft verification failed: workload IP '{requiredWorkloadIp}' was not found in spoof_drop rule.");
        }

        var options = new JsonSerializerOptions { WriteIndented = false };
        string canonicalJson = canonicalArray.ToJsonString(options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static JsonNode CanonicalizeNode(
        JsonNode node,
        HashSet<string> foundComments,
        ref bool foundWorkloadIp,
        string? expectedIp)
    {
        if (node is JsonObject obj)
        {
            var canonicalObj = new JsonObject();
            // Sort keys deterministically:
            var sortedKeys = obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

            foreach (string key in sortedKeys)
            {
                // Strip dynamic/ephemeral handle:
                if (string.Equals(key, "handle", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonNode? child = obj[key];
                if (child is null)
                {
                    canonicalObj[key] = null;
                    continue;
                }

                // Check comments:
                if (string.Equals(key, "comment", StringComparison.Ordinal) && child is JsonValue commentVal)
                {
                    string? commentStr = commentVal.GetValue<string?>();
                    if (!string.IsNullOrWhiteSpace(commentStr))
                    {
                        foundComments.Add(commentStr.Trim());
                    }
                }

                // Canonicalize counter objects (zero packets and bytes):
                if (string.Equals(key, "counter", StringComparison.Ordinal))
                {
                    var zeroCounter = new JsonObject
                    {
                        ["bytes"] = 0,
                        ["packets"] = 0,
                    };
                    canonicalObj[key] = zeroCounter;
                    continue;
                }

                // Detect IP match in expressions:
                if (!string.IsNullOrWhiteSpace(expectedIp))
                {
                    string childText = child.ToJsonString();
                    if (childText.Contains(expectedIp, StringComparison.Ordinal))
                    {
                        foundWorkloadIp = true;
                    }
                }

                canonicalObj[key] = CanonicalizeNode(child, foundComments, ref foundWorkloadIp, expectedIp);
            }

            return canonicalObj;
        }

        if (node is JsonArray arr)
        {
            var canonicalArr = new JsonArray();
            foreach (JsonNode? item in arr)
            {
                if (item is not null)
                {
                    canonicalArr.Add(CanonicalizeNode(item, foundComments, ref foundWorkloadIp, expectedIp));
                }
            }

            return canonicalArr;
        }

        return node.DeepClone();
    }
}
