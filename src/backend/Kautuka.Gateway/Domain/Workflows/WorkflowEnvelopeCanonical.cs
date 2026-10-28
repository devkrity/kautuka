using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowEnvelopeCanonical
{
    public const int MaxUtf8Bytes = 256 * 1024;

    public static void EnsureSafe(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ContainsSecret(node))
        {
            throw new WorkflowSchemaException("Envelope contains a secret canary.");
        }

        int bytes = Encoding.UTF8.GetByteCount(node.ToJsonString());
        if (bytes > MaxUtf8Bytes)
        {
            throw new WorkflowSchemaException("Envelope exceeds size cap.");
        }
    }

    public static string Sha256Hex(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        byte[] utf8 = Encoding.UTF8.GetBytes(Canonicalize(node).ToJsonString());
        return Convert.ToHexString(SHA256.HashData(utf8));
    }

    public static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => CanonicalizeObject(obj),
        JsonArray array => CanonicalizeArray(array),
        JsonValue value => JsonNode.Parse(value.ToJsonString())!,
        _ => throw new InvalidOperationException("Unsupported JSON node."),
    };

    public static bool ContainsSecret(JsonNode node)
    {
        foreach (string text in WalkStrings(node))
        {
            if (LooksLikeSecret(text))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool LooksLikeSecret(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("BEGIN ", StringComparison.Ordinal)
            && (text.Contains("PRIVATE KEY", StringComparison.Ordinal) || text.Contains("CERTIFICATE", StringComparison.Ordinal)))
        {
            return true;
        }

        return text.StartsWith("sk-", StringComparison.Ordinal)
            || text.StartsWith("ghp_", StringComparison.Ordinal)
            || text.StartsWith("xoxb-", StringComparison.Ordinal)
            || text.StartsWith("xoxp-", StringComparison.Ordinal);
    }

    private static JsonObject CanonicalizeObject(JsonObject obj)
    {
        var ordered = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ordered[pair.Key] = pair.Value is null ? null : Canonicalize(pair.Value);
        }

        return ordered;
    }

    private static JsonArray CanonicalizeArray(JsonArray array)
    {
        var clone = new JsonArray();
        foreach (JsonNode? item in array)
        {
            clone.Add(item is null ? null : Canonicalize(item));
        }

        return clone;
    }

    private static IEnumerable<string> WalkStrings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    if (pair.Value is not null)
                    {
                        foreach (string text in WalkStrings(pair.Value))
                        {
                            yield return text;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is not null)
                    {
                        foreach (string text in WalkStrings(item))
                        {
                            yield return text;
                        }
                    }
                }

                break;
            case JsonValue value when value.TryGetValue(out string? text) && text is not null:
                yield return text;
                break;
        }
    }
}
